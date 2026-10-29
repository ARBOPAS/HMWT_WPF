namespace HMWT_MUI.Helpers;

public static class DonateInfo
{
	public const string CardNumber = "";

	public const string SbpPhone = "";

	public const string Recipient = "";

	public const string LinkUrl = "https://boosty.to/arbopas";

	public const string LinkTitle = "Поддержать на Boosty";

	public static bool HasCard => !string.IsNullOrWhiteSpace("");

	public static bool HasSbp => !string.IsNullOrWhiteSpace("");

	public static bool HasLink => !string.IsNullOrWhiteSpace("https://boosty.to/arbopas");

	public static bool HasRecipient => !string.IsNullOrWhiteSpace("");

	public static bool IsConfigured
	{
		get
		{
			if (!HasCard && !HasSbp)
			{
				return HasLink;
			}
			return true;
		}
	}

	public static string CardForCopy => "".Replace(" ", "").Replace("-", "").Replace("\u00a0", "");

	public static string SbpForCopy => "".Replace(" ", "").Replace("-", "").Replace("(", "")
		.Replace(")", "")
		.Replace("\u00a0", "");
}
