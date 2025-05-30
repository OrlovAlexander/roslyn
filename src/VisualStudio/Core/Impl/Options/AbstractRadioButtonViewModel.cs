﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using Microsoft.VisualStudio.LanguageServices.Implementation.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Options;

internal abstract class AbstractRadioButtonViewModel : AbstractNotifyPropertyChanged
{
    private readonly AbstractOptionPreviewViewModel _info;
    internal readonly string Preview;
    private bool _isChecked;

    public string Description { get; }
    public string GroupName { get; }

    public bool IsChecked
    {
        get
        {
            return _isChecked;
        }

        set
        {
            SetProperty(ref _isChecked, value);

            if (_isChecked)
            {
                SetOptionAndUpdatePreview(_info, Preview);
            }
        }
    }

    public AbstractRadioButtonViewModel(string description, string preview, AbstractOptionPreviewViewModel info, bool isChecked, string group)
    {
        Description = description;
        this.Preview = preview;
        _info = info;
        this.GroupName = group;

        SetProperty(ref _isChecked, isChecked);
    }

    internal abstract void SetOptionAndUpdatePreview(AbstractOptionPreviewViewModel info, string preview);
}
