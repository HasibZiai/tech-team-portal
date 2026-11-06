using Microsoft.AspNetCore.Components;
using MudBlazor;
using PcServ.Web.Components.Shared;

namespace PcServ.Web.Services;

public static class DialogExtensions
{
    private static readonly DialogOptions FormOptions = new()
    {
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
        CloseButton = true,
    };

    /// <summary>Shows a yes/no confirmation. Returns true if the user confirmed.</summary>
    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string title, string message,
        string confirmText = "Confirm", bool danger = false)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { d => d.Title, title },
            { d => d.Message, message },
            { d => d.ConfirmText, confirmText },
            { d => d.Danger, danger },
        };
        var dialog = await dialogs.ShowAsync<ConfirmDialog>(title, parameters, new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    /// <summary>
    /// Opens an edit dialog for <paramref name="model"/> and returns the edited copy, or null if cancelled.
    /// The dialog component must have a [Parameter] named "Model".
    /// </summary>
    public static async Task<TModel?> EditAsync<TDialog, TModel>(this IDialogService dialogs, string title, TModel model,
        Dictionary<string, object?>? extraParameters = null)
        where TDialog : IComponent
        where TModel : class
    {
        var parameters = new DialogParameters { ["Model"] = model };
        if (extraParameters is not null)
        {
            foreach (var (key, value) in extraParameters) parameters[key] = value;
        }

        var dialog = await dialogs.ShowAsync<TDialog>(title, parameters, FormOptions);
        var result = await dialog.Result;
        return result is { Canceled: false } ? result.Data as TModel : null;
    }
}
