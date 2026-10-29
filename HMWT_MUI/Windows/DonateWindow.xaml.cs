using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Windows;

public partial class DonateWindow : Window, IComponentConnector
{
	private static readonly TimeSpan CopyFeedbackTime = TimeSpan.FromSeconds(1.6);

	public DonateWindow()
	{
		InitializeComponent();
		FillFromSettings();
	}

	private void FillFromSettings()
	{
		bool isConfigured = DonateInfo.IsConfigured;
		CardRow.Visibility = ((!DonateInfo.HasCard) ? Visibility.Collapsed : Visibility.Visible);
		SbpRow.Visibility = ((!DonateInfo.HasSbp) ? Visibility.Collapsed : Visibility.Visible);
		btnOpenLink.Visibility = ((!DonateInfo.HasLink) ? Visibility.Collapsed : Visibility.Visible);
		EmptyHint.Visibility = (isConfigured ? Visibility.Collapsed : Visibility.Visible);
		RequisitesBlock.Visibility = ((!isConfigured) ? Visibility.Collapsed : Visibility.Visible);
		txtCard.Text = "";
		txtSbp.Text = "";
		RecipientText.Visibility = ((!DonateInfo.HasRecipient) ? Visibility.Collapsed : Visibility.Visible);
		RecipientText.Text = (DonateInfo.HasRecipient ? "Получатель: " : "");
		btnOpenLink.Content = string.IsNullOrWhiteSpace(DonateInfo.LinkTitle) ? "Открыть страницу поддержки" : DonateInfo.LinkTitle;
		txtLinkUrl.Text = DonateInfo.LinkUrl;
		txtLinkUrl.Visibility = ((!DonateInfo.HasLink) ? Visibility.Collapsed : Visibility.Visible);
		if (DonateInfo.HasCard || DonateInfo.HasSbp)
		{
			FooterNote.Text = "Реквизиты копируются в буфер обмена: вставлять их нужно в приложении банка, а не в программе.";
		}
		else
		{
			FooterNote.Text = "Кнопка откроет страницу поддержки в браузере — там можно поддержать разово или подпиской.";
		}
	}

	private void BtnCopyCard_Click(object sender, RoutedEventArgs e)
	{
		CopyToClipboard(DonateInfo.CardForCopy, btnCopyCard);
	}

	private void BtnCopySbp_Click(object sender, RoutedEventArgs e)
	{
		CopyToClipboard(DonateInfo.SbpForCopy, btnCopySbp);
	}

	private void CopyToClipboard(string value, Button button)
	{
		if (string.IsNullOrWhiteSpace(value) || button == null)
		{
			return;
		}
		try
		{
			Clipboard.SetText(value);
		}
		catch (Exception)
		{
			MessageDialog.Info(this, "Не удалось скопировать автоматически. Выделите номер в поле и нажмите Ctrl+C.", "Буфер обмена занят");
			return;
		}
		string caption = (button.Content as string) ?? "Скопировать";
		button.Content = "Скопировано ✓";
		button.IsEnabled = false;
		DispatcherTimer timer = new DispatcherTimer
		{
			Interval = CopyFeedbackTime
		};
		timer.Tick += (object s, EventArgs e) =>
		{
			timer.Stop();
			if (button.IsLoaded)
			{
				button.Content = caption;
				button.IsEnabled = true;
			}
		};
		timer.Start();
	}

	private void BtnOpenLink_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(DonateInfo.LinkUrl))
		{
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(DonateInfo.LinkUrl)
			{
				UseShellExecute = true
			});
		}
		catch (Exception)
		{
			MessageDialog.Info(this, "Не удалось открыть ссылку: " + DonateInfo.LinkUrl, "Ссылка");
		}
	}

	private void BtnClose_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void Window_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Close();
		}
	}
}
