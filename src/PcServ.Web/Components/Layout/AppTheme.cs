using MudBlazor;

namespace PcServ.Web.Components.Layout;

/// <summary>
/// App colors, built on navy blue and chosen for readability first. Every text color here is meant
/// to meet WCAG AA contrast (at least 4.5:1) on the backgrounds it's used on, in both modes.
/// Check any change with an accessibility tool: a color that looks fine can still fail.
/// </summary>
public static class AppTheme
{
    private static readonly string[] FontStack =
        ["Segoe UI", "system-ui", "-apple-system", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#12315C",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#2F4A6D",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#1B5E9E",
            TertiaryContrastText = "#FFFFFF",
            Info = "#1B5E9E",
            InfoContrastText = "#FFFFFF",
            Success = "#1B6E36",
            SuccessContrastText = "#FFFFFF",
            Warning = "#7A5200",
            WarningContrastText = "#FFFFFF",
            Error = "#B3261E",
            ErrorContrastText = "#FFFFFF",
            AppbarBackground = "#0B1F3A",
            AppbarText = "#FFFFFF",
            Background = "#F4F6F9",
            Surface = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#14233A",
            DrawerIcon = "#2F4A6D",
            TextPrimary = "#0F1C2E",
            TextSecondary = "#3D4C5F",
            TextDisabled = "#5A6675",
            ActionDefault = "#2F4A6D",
            ActionDisabled = "#5A6675",
            LinesDefault = "#C5CEDA",
            LinesInputs = "#6B7A8C",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#9CC7FF",
            PrimaryContrastText = "#0B1F3A",
            Secondary = "#BBD0EA",
            SecondaryContrastText = "#0B1F3A",
            Tertiary = "#9CC7FF",
            TertiaryContrastText = "#0B1F3A",
            Info = "#9CC7FF",
            InfoContrastText = "#0B1F3A",
            Success = "#86D9A0",
            SuccessContrastText = "#0B1F3A",
            Warning = "#F2CF66",
            WarningContrastText = "#0B1F3A",
            Error = "#FFB4AB",
            ErrorContrastText = "#0B1F3A",
            AppbarBackground = "#0B1F3A",
            AppbarText = "#FFFFFF",
            Background = "#0F1B2D",
            Surface = "#18263B",
            DrawerBackground = "#14213A",
            DrawerText = "#EAF0F8",
            DrawerIcon = "#BBD0EA",
            TextPrimary = "#F1F5FA",
            TextSecondary = "#C9D4E2",
            TextDisabled = "#AEBACA",
            ActionDefault = "#BBD0EA",
            ActionDisabled = "#AEBACA",
            LinesDefault = "#3A4B63",
            LinesInputs = "#8496AD",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack },
            H1 = new H1Typography { FontFamily = FontStack },
            H2 = new H2Typography { FontFamily = FontStack },
            H3 = new H3Typography { FontFamily = FontStack },
            H4 = new H4Typography { FontFamily = FontStack, FontWeight = "600" },
            H5 = new H5Typography { FontFamily = FontStack, FontWeight = "600" },
            H6 = new H6Typography { FontFamily = FontStack, FontWeight = "600" },
            Body1 = new Body1Typography { FontFamily = FontStack, FontSize = "1rem", LineHeight = "1.55" },
            Body2 = new Body2Typography { FontFamily = FontStack, FontSize = "0.9375rem", LineHeight = "1.5" },
            Caption = new CaptionTypography { FontFamily = FontStack, FontSize = "0.8125rem", LineHeight = "1.45" },
            Button = new ButtonTypography { FontFamily = FontStack, TextTransform = "none", FontWeight = "600" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
