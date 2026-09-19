using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using iText.Kernel.Pdf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDF_simple_edit.Models;

namespace PDF_simple_edit.Services;

public sealed class PdfProtectionDialogService
{
    public async Task<PdfProtectionSettings?> ShowAsync(XamlRoot root, PdfProtectionSettings current)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = "변경 사항은 PDF를 저장할 때 반영됩니다. 비밀번호를 삭제하려면 해당 항목의 체크를 해제하세요.",
            TextWrapping = TextWrapping.Wrap
        });
        if (current.NeedsPasswords)
            panel.Children.Add(new TextBlock
            {
                Text = "이 파일의 기존 비밀번호 중 일부를 알 수 없습니다. 아래에 비밀번호를 직접 입력하면 저장에 사용됩니다. 기존 값을 유지하려면 동일한 비밀번호를 입력하세요.",
                TextWrapping = TextWrapping.Wrap
            });

        var algorithm = new ComboBox { Header = "암호화 방식", HorizontalAlignment = HorizontalAlignment.Stretch };
        algorithm.Items.Add("AES-256 (기본)");
        algorithm.Items.Add("AES-128 (호환성)");
        algorithm.SelectedIndex = current.Algorithm == PdfEncryptionAlgorithm.Aes128 ? 1 : 0;
        panel.Children.Add(algorithm);
        var metadata = new CheckBox { Content = "문서 메타데이터도 암호화", IsChecked = current.EncryptMetadata };
        panel.Children.Add(metadata);

        var useOpen = new CheckBox { Content = "열기 비밀번호 (Open)", IsChecked = current.RequireOpenPassword };
        var open = MakePasswordBox("열기 비밀번호", current.OpenPassword);
        var openConfirm = MakePasswordBox("열기 비밀번호 확인", null);
        var openPanel = new StackPanel { Spacing = 8 };
        openPanel.Children.Add(open);
        openPanel.Children.Add(openConfirm);
        panel.Children.Add(useOpen);
        panel.Children.Add(openPanel);

        var useOwner = new CheckBox { Content = "권한 비밀번호 (Permission / Owner)", IsChecked = current.RequireOwnerPassword };
        var owner = MakePasswordBox("권한 비밀번호", current.OwnerPassword);
        var ownerConfirm = MakePasswordBox("권한 비밀번호 확인", null);
        var ownerPanel = new StackPanel { Spacing = 8 };
        ownerPanel.Children.Add(owner);
        ownerPanel.Children.Add(ownerConfirm);
        ownerPanel.Children.Add(new TextBlock
        {
            Text = "권한 비밀번호로 사용 제한을 설정합니다. PDF 프로그램에 따라 제한 적용 방식이 다를 수 있습니다.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
        });

        var printing = new ComboBox { Header = "인쇄 허용", HorizontalAlignment = HorizontalAlignment.Stretch };
        printing.Items.Add("허용하지 않음");
        printing.Items.Add("저해상도만 허용");
        printing.Items.Add("고해상도 허용");
        printing.SelectedIndex = (current.Permissions & EncryptionConstants.ALLOW_PRINTING) == EncryptionConstants.ALLOW_PRINTING
            ? 2 : (current.Permissions & EncryptionConstants.ALLOW_DEGRADED_PRINTING) != 0 ? 1 : 0;
        ownerPanel.Children.Add(printing);
        var permissions = new List<(CheckBox Box, int Flag)>();
        foreach (var (label, flag) in new[]
        {
            ("내용 편집", EncryptionConstants.ALLOW_MODIFY_CONTENTS),
            ("텍스트·이미지 복사", EncryptionConstants.ALLOW_COPY),
            ("주석 추가·수정", EncryptionConstants.ALLOW_MODIFY_ANNOTATIONS),
            ("양식 작성", EncryptionConstants.ALLOW_FILL_IN),
            ("페이지 삽입·삭제·회전", EncryptionConstants.ALLOW_ASSEMBLY)
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
        var dialog = new ContentDialog
        {
            Title = "암호화 및 비밀번호 설정",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Max(180, Math.Min(580, root.Size.Height - 220)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = "적용", CloseButtonText = "취소", XamlRoot = root,
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? message = null;
            if (useOpen.IsChecked == true && open.Password != openConfirm.Password)
                message = "열기 비밀번호와 확인 입력이 일치하지 않습니다.";
            else if (useOwner.IsChecked == true && owner.Password != ownerConfirm.Password)
                message = "권한 비밀번호와 확인 입력이 일치하지 않습니다.";
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
        await new ContentDialog
        {
            Title = "모든 비밀번호 삭제 / 보호 해제",
            Content = "열기·권한 비밀번호, 암호화, 사용 권한 제한을 모두 제거합니다. PDF를 저장하면 비밀번호 없이 열 수 있습니다.\n\n비밀번호를 개별 삭제하려면 ‘암호화 및 비밀번호 설정·변경’을 사용하세요.",
            PrimaryButtonText = "보호 해제", CloseButtonText = "취소", XamlRoot = root
        }.ShowAsync() == ContentDialogResult.Primary;

    private static PasswordBox MakePasswordBox(string label, string? known) => new()
    {
        Header = label,
        PlaceholderText = string.IsNullOrEmpty(known) ? "비밀번호 입력" : "비워 두면 기존 비밀번호 유지",
        PasswordRevealMode = PasswordRevealMode.Peek
    };

    private static string? KeepOrReplace(string input, string? known) => input.Length == 0 ? known : input;

    private static void AutomationPropertiesHelper(TextBlock error) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
}
