using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using PcServ.Core.Entities;
using PcServ.Core.Personality;
using PcServ.Web.Services;

namespace PcServ.Web.Components.Shared;

/// <summary>
/// Base class for pages that list records and let managers add, edit and delete them.
/// Handles saving, deleting, success messages and reloading the list.
/// </summary>
public abstract class RecordPageBase : ComponentBase
{
    [Inject] protected AppDbFactory DbFactory { get; set; } = default!;
    [Inject] protected IDialogService Dialogs { get; set; } = default!;
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;
    [Inject] protected SettingsService Settings { get; set; } = default!;

    /// <summary>Reloads the page's data after a change.</summary>
    protected abstract Task LoadAsync();

    /// <summary>Message shown when a save hits a uniqueness rule (e.g. duplicate name).</summary>
    protected virtual string DuplicateMessage => "Couldn't save. Something with that name may already exist.";

    protected override Task OnInitializedAsync() => LoadAsync();

    protected async Task SaveAsync<T>(T model) where T : Entity, new()
    {
        try
        {
            await DbFactory.SaveAsync(model);
            Snackbar.Add(await Settings.QuipAsync(QuipMoment.Saved), Severity.Success);
        }
        catch (DbUpdateException)
        {
            Snackbar.Add(DuplicateMessage, Severity.Error);
        }
        catch (InvalidOperationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        await LoadAsync();
    }

    protected async Task DeleteAsync<T>(int id) where T : Entity
    {
        await DbFactory.DeleteAsync<T>(id);
        Snackbar.Add(await Settings.QuipAsync(QuipMoment.Deleted), Severity.Info);
        await LoadAsync();
    }
}
