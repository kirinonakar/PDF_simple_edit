using Loc = PDF_simple_edit.Services.LocalizationService;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using iText.Kernel.Pdf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Controls;
using PDF_simple_edit.Models;

namespace PDF_simple_edit.Services;

public sealed class PdfProtectionDialogService
{
    public async Task<PdfProtectionSettings?> ShowAsync(XamlRoot root, PdfProtectionSettings current)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("ChangesTakeEffectWhenYouSaveThePDFClear"),
            TextWrapping = TextWrapping.Wrap
        });
        if (current.NeedsPasswords)
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Get("SomeExistingPasswordsForThisFileAreUnknownEnter"),
                TextWrapping = TextWrapping.Wrap
            });

        var algorithm = new ComboBox { Header = Loc.Get("EncryptionAlgorithm"), HorizontalAlignment = HorizontalAlignment.Stretch };
        algorithm.Items.Add(Loc.Get("AES256Default"));
        algorithm.Items.Add(Loc.Get("AES128Compatibility"));
        algorithm.SelectedIndex = current.Algorithm == PdfEncryptionAlgorithm.Aes128 ? 1 : 0;
        panel.Children.Add(algorithm);
        var metadata = new CheckBox { Content = Loc.Get("EncryptDocumentMetadata"), IsChecked = current.EncryptMetadata };
        panel.Children.Add(metadata);

        var useOpen = new CheckBox { Content = Loc.Get("OpenPasswordOpen"), IsChecked = current.RequireOpenPassword };
        var open = MakePasswordBox(Loc.Get("OpenPassword"), current.OpenPassword);
        var openConfirm = MakePasswordBox(Loc.Get("ConfirmOpenPassword"), null);
        var openPanel = new StackPanel { Spacing = 8 };
        openPanel.Children.Add(open);
        openPanel.Children.Add(openConfirm);
        panel.Children.Add(useOpen);
        panel.Children.Add(openPanel);

        var useOwner = new CheckBox { Content = Loc.Get("PermissionsPasswordPermissionOwner"), IsChecked = current.RequireOwnerPassword };
        var owner = MakePasswordBox(Loc.Get("PermissionsPassword"), current.OwnerPassword);
        var ownerConfirm = MakePasswordBox(Loc.Get("ConfirmPermissionsPassword"), null);
        var ownerPanel = new StackPanel { Spacing = 8 };
        ownerPanel.Children.Add(owner);
        ownerPanel.Children.Add(ownerConfirm);
        ownerPanel.Children.Add(new TextBlock
        {
            Text = Loc.Get("UseThePermissionsPasswordToRestrictUsageRestrictionsMay"),
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
        });

        var printing = new ComboBox { Header = Loc.Get("AllowPrinting"), HorizontalAlignment = HorizontalAlignment.Stretch };
        printing.Items.Add(Loc.Get("NotAllowed"));
        printing.Items.Add(Loc.Get("LowResolutionOnly"));
        printing.Items.Add(Loc.Get("HighResolutionAllowed"));
        printing.SelectedIndex = (current.Permissions & EncryptionConstants.ALLOW_PRINTING) == EncryptionConstants.ALLOW_PRINTING
            ? 2 : (current.Permissions & EncryptionConstants.ALLOW_DEGRADED_PRINTING) != 0 ? 1 : 0;
        ownerPanel.Children.Add(printing);
        var permissions = new List<(CheckBox Box, int Flag)>();
        foreach (var (label, flag) in new[]
        {
            (Loc.Get("EditContent"), EncryptionConstants.ALLOW_MODIFY_CONTENTS),
            (Loc.Get("CopyTextAndImages"), EncryptionConstants.ALLOW_COPY),
            (Loc.Get("AddAndEditAnnotations"), EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS),
            (Loc.Get("FillInForms"), EncryptionConstants.ALLOW_FILL_IN),
            (Loc.Get("InsertDeleteAndRotatePages"), EncryptionConstants.ALLOW_ASSEMBLY)
        })
        {
            var box = new CheckBox { Content = label, IsChecked = (current.Permissions & flag) == flag };
            permissions.Add((box, flag));
            ownerPanel.Children.Add(box);
        }
        panel.Children.Add(useOwner);
        panel.Children.Add(ownerPanel);
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            Visibility = Visibility.Collapsed
        };
        AutomationPropertiesHelper(error);
        panel.Children.Add(error);

        void Refresh()
        {
            openPanel.Visibility = useOpen.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ownerPanel.Visibility = useOwner.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            metadata.IsEnabled = algorithm.IsEnabled = useOpen.IsChecked == true || useOwner.IsChecked == true;
        }
        useOpen.Checked += (_, _) => Refresh();
        useOpen.Unchecked += (_, _) => Refresh();
        useOwner.Checked += (_, _) => Refresh();
        useOwner.Unchecked += (_, _) => Refresh();
        Refresh();

        PdfProtectionSettings? selected = null;
        var dialog = new EditorContentDialog(root)
        {
            Title = Loc.Get("EncryptionAndPasswordSettings"),
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Max(180, Math.Min(580, root.Size.Height - 220)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = Loc.Get("Apply"), CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? message = null;
            if (useOpen.IsChecked == true && open.Password != openConfirm.Password)
                message = Loc.Get("TheOpenPasswordAndConfirmationDoNotMatch");
            else if (useOwner.IsChecked == true && owner.Password != ownerConfirm.Password)
                message = Loc.Get("ThePermissionsPasswordAndConfirmationDoNotMatch");
            int flags = EncryptionConstants.ALLOW_SCREENREADERS;
            flags |= printing.SelectedIndex switch
            {
                2 => EncryptionConstants.ALLOW_PRINTING,
                1 => EncryptionConstants.ALLOW_DEGRADED_PRINTING,
                _ => 0
            };
            foreach (var (box, flag) in permissions)
                if (box.IsChecked == true) flags |= flag;
            selected = new PdfProtectionSettings
            {
                Enabled = useOpen.IsChecked == true || useOwner.IsChecked == true,
                Algorithm = algorithm.SelectedIndex == 1 ? PdfEncryptionAlgorithm.Aes128 : PdfEncryptionAlgorithm.Aes256,
                EncryptMetadata = metadata.IsChecked == true,
                RequireOpenPassword = useOpen.IsChecked == true,
                RequireOwnerPassword = useOwner.IsChecked == true,
                OpenPassword = useOpen.IsChecked == true ? KeepOrReplace(open.Password, current.OpenPassword) : null,
                OwnerPassword = useOwner.IsChecked == true ? KeepOrReplace(owner.Password, current.OwnerPassword) : null,
                Permissions = useOwner.IsChecked == true ? flags : PdfSecurityService.AllPermissions
            };
            message ??= selected.GetValidationError();
            if (message != null)
            {
                selected = null;
                args.Cancel = true;
                error.Text = message;
                error.Visibility = Visibility.Visible;
                // The error stays visible even when permission controls require scrolling.
                (dialog.Content as ScrollViewer)?.ChangeView(null, double.MaxValue, null);
            }
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? selected : null;
        }
        finally
        {
            open.Password = openConfirm.Password = owner.Password = ownerConfirm.Password = "";
        }
    }

    public async Task<bool> ConfirmRemoveAsync(XamlRoot root) =>
        await new EditorContentDialog(root)
        {
            Title = Loc.Get("RemoveAllPasswordsRemoveProtection"),
            Content = Loc.Get("RemoveAllOpenAndPermissionsPasswordsEncryptionAndUsage"),
            PrimaryButtonText = Loc.Get("RemoveProtection"), CloseButtonText = Loc.Get("Cancel")
        }.ShowAsync() == ContentDialogResult.Primary;

    private static PasswordBox MakePasswordBox(string label, string? known) => new()
    {
        Header = label,
        PlaceholderText = string.IsNullOrEmpty(known) ? Loc.Get("EnterPassword2") : Loc.Get("LeaveBlankToKeepTheExistingPassword"),
        PasswordRevealMode = PasswordRevealMode.Peek
    };

    private static string? KeepOrReplace(string input, string? known) => input.Length == 0 ? known : input;

    private static void AutomationPropertiesHelper(TextBlock error) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
}
