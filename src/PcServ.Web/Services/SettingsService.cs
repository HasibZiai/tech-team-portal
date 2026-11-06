using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PcServ.Core.Entities;
using PcServ.Core.Personality;
using PcServ.Data;

namespace PcServ.Web.Services;

/// <summary>
/// Cached access to app-wide settings (stored in the AppSettings table).
/// Raises <see cref="Changed"/> so open pages can update immediately.
/// </summary>
public sealed class SettingsService(IDbContextFactory<PcServDbContext> factory)
{
    private readonly ConcurrentDictionary<string, string?> _cache = new();
    private volatile bool _loaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public event Action? Changed;

    /// <summary>Default values used until an admin changes them.</summary>
    private static readonly Dictionary<string, string> Defaults = new()
    {
        [SettingKeys.PersonalityCore] = "false",
        [SettingKeys.OrganizationName] = "IT Team",
    };

    public async Task<string?> GetAsync(string key)
    {
        await EnsureLoadedAsync();
        return _cache.TryGetValue(key, out var value) ? value : Defaults.GetValueOrDefault(key);
    }

    public async Task<bool> GetBoolAsync(string key) =>
        bool.TryParse(await GetAsync(key), out var b) && b;

    public async Task SetAsync(string key, string? value, string changedBy)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentUserName = changedBy;

        var setting = await db.Settings.FindAsync(key);
        var old = setting?.Value ?? Defaults.GetValueOrDefault(key);
        if (setting is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        db.LogAudit("Setting.Changed", nameof(AppSetting), key, $"{key}: \"{old}\" → \"{value}\"");
        await db.SaveChangesAsync();

        _cache[key] = value;
        Changed?.Invoke();
    }

    public Task<bool> IsPersonalityEnabledAsync() => GetBoolAsync(SettingKeys.PersonalityCore);

    /// <summary>A line for the given moment, in GLaDOS mode or plain mode depending on the setting.</summary>
    public async Task<string> QuipAsync(QuipMoment moment) =>
        Quips.For(moment, await IsPersonalityEnabledAsync());

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _loadLock.WaitAsync();
        try
        {
            if (_loaded) return;
            await using var db = await factory.CreateDbContextAsync();
            foreach (var s in await db.Settings.AsNoTracking().ToListAsync())
            {
                _cache[s.Key] = s.Value;
            }
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
