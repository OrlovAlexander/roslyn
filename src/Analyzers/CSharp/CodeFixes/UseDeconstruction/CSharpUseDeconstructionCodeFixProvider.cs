﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.CSharp.UseDeconstruction;

using static SyntaxFactory;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = PredefinedCodeFixProviderNames.UseDeconstruction), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CSharpUseDeconstructionCodeFixProvider() : SyntaxEditorBasedCodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds
        => [IDEDiagnosticIds.UseDeconstructionDiagnosticId];

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        RegisterCodeFix(context, CSharpAnalyzersResources.Deconstruct_variable_declaration, nameof(CSharpAnalyzersResources.Deconstruct_variable_declaration));
        return Task.CompletedTask;
    }

    protected override Task FixAllAsync(
        Document document, ImmutableArray<Diagnostic> diagnostics,
        SyntaxEditor editor, CancellationToken cancellationToken)
    {
        var nodesToProcess = diagnostics.SelectAsArray(d => d.Location.FindToken(cancellationToken).GetRequiredParent());

        // When doing a fix all, we have to avoid introducing the same name multiple times
        // into the same scope.  However, checking results after each change would be very
        // expensive (lots of forking + new semantic models, etc.).  So we use 
        // ApplyMethodBodySemanticEditsAsync to help out here.  It will only do the forking
        // if there are multiple results in the same method body.  If there's only one 
        // result in a method body, we will just apply it without doing any extra analysis.
        return editor.ApplyMethodBodySemanticEditsAsync(
            document, nodesToProcess,
            (semanticModel, node) => true,
            (semanticModel, currentRoot, node) => UpdateRoot(document, semanticModel, currentRoot, node, cancellationToken),
            cancellationToken);
    }

    private SyntaxNode UpdateRoot(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode root,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        var editor = new SyntaxEditor(root, document.Project.Solution.Services);

        // We use the callback form of ReplaceNode because we may have nested code that
        // needs to be updated in fix-all situations.  For example, nested foreach statements.
        // We need to see the results of the inner changes when making the outer changes.

        ImmutableArray<MemberAccessExpressionSyntax> memberAccessExpressions = default;
        if (node is VariableDeclaratorSyntax variableDeclarator)
        {
            var variableDeclaration = (VariableDeclarationSyntax)variableDeclarator.GetRequiredParent();
            if (CSharpUseDeconstructionDiagnosticAnalyzer.TryAnalyzeVariableDeclaration(
                    semanticModel, variableDeclaration,
                    out var tupleType, out memberAccessExpressions,
                    cancellationToken))
            {
                editor.ReplaceNode(
                    variableDeclaration.GetRequiredParent(),
                    (current, _) =>
                    {
                        var currentDeclarationStatement = (LocalDeclarationStatementSyntax)current;
                        return CreateDeconstructionStatement(tupleType, currentDeclarationStatement, currentDeclarationStatement.Declaration.Variables[0]);
                    });
            }
        }
        else if (node is ForEachStatementSyntax forEachStatement)
        {
            if (CSharpUseDeconstructionDiagnosticAnalyzer.TryAnalyzeForEachStatement(
                    semanticModel, forEachStatement,
                    out var tupleType, out memberAccessExpressions,
                    cancellationToken))
            {
                editor.ReplaceNode(
                    forEachStatement,
                    (current, _) => CreateForEachVariableStatement(tupleType, (ForEachStatementSyntax)current));
            }
        }

        foreach (var memberAccess in memberAccessExpressions.NullToEmpty())
        {
            editor.ReplaceNode(
                memberAccess,
                (current, _) =>
                {
                    var currentMemberAccess = (MemberAccessExpressionSyntax)current;
                    return currentMemberAccess.Name.WithTriviaFrom(currentMemberAccess);
                });
        }

        return editor.GetChangedRoot();
    }

    private ForEachVariableStatementSyntax CreateForEachVariableStatement(INamedTypeSymbol tupleType, ForEachStatementSyntax forEachStatement)
    {
        // Copy all the tokens/nodes from the existing foreach statement to the new foreach statement.
        // However, convert the existing declaration over to a "var (x, y)" declaration or (int x, int y)
        // tuple expression.
        return ForEachVariableStatement(
            forEachStatement.AttributeLists,
            forEachStatement.AwaitKeyword,
            forEachStatement.ForEachKeyword,
            forEachStatement.OpenParenToken,
            CreateTupleOrDeclarationExpression(tupleType, forEachStatement.Type),
            forEachStatement.InKeyword,
            forEachStatement.Expression,
            forEachStatement.CloseParenToken,
            forEachStatement.Statement);
    }

    private ExpressionStatementSyntax CreateDeconstructionStatement(
        INamedTypeSymbol tupleType, LocalDeclarationStatementSyntax declarationStatement, VariableDeclaratorSyntax variableDeclarator)
    {
        // Copy all the tokens/nodes from the existing declaration statement to the new assignment
        // statement. However, convert the existing declaration over to a "var (x, y)" declaration 
        // or (int x, int y) tuple expression.
        return ExpressionStatement(
            AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                CreateTupleOrDeclarationExpression(tupleType, declarationStatement.Declaration.Type),
                variableDeclarator.Initializer!.EqualsToken,
                variableDeclarator.Initializer.Value),
            declarationStatement.SemicolonToken);
    }

    private ExpressionSyntax CreateTupleOrDeclarationExpression(INamedTypeSymbol tupleType, TypeSyntax typeNode)
    {
        // If we have an explicit tuple type in code, convert that over to a tuple expression.
        // i.e.   (int x, int y) t = ...   will be converted to (int x, int y) = ...
        //
        // If we had the "var t" form we'll convert that to the declaration expression "var (x, y)"
        return typeNode is TupleTypeSyntax tupleTypeSyntax
            ? CreateTupleExpression(tupleTypeSyntax)
            : CreateDeclarationExpression(tupleType, typeNode);
    }

    private static DeclarationExpressionSyntax CreateDeclarationExpression(INamedTypeSymbol tupleType, TypeSyntax typeNode)
        => DeclarationExpression(
            typeNode, ParenthesizedVariableDesignation(
                [.. tupleType.TupleElements.Select(
                    e => SingleVariableDesignation(Identifier(e.Name.EscapeIdentifier())))]));

    private TupleExpressionSyntax CreateTupleExpression(TupleTypeSyntax typeNode)
        => TupleExpression(
            typeNode.OpenParenToken,
            SeparatedList<ArgumentSyntax>([.. typeNode.Elements.GetWithSeparators().Select(ConvertTupleTypeElementComponent)]),
            typeNode.CloseParenToken);

    private SyntaxNodeOrToken ConvertTupleTypeElementComponent(SyntaxNodeOrToken nodeOrToken)
    {
        if (nodeOrToken.IsToken)
        {
            // return commas directly as is.
            return nodeOrToken;
        }

        // "int x" as a tuple element directly translates to "int x" (a declaration expression
        // with a variable designation 'x').
        var node = (TupleElementSyntax)nodeOrToken.AsNode()!;
        return Argument(
            DeclarationExpression(
                node.Type,
                SingleVariableDesignation(node.Identifier)));
    }
}
