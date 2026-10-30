using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// A minimal <see cref="ILibraryManager"/> (through <see cref="DispatchProxy"/>, the interface is large): item ids
/// derived the way Jellyfin 12.3's <c>LibraryManager.GetNewItemId</c> derives them, items by id, one item list for
/// the live-item query (filtered by external id), and a record of every <c>UpdateItemAsync</c>. Anything else throws.
/// </summary>
public class FakeLibrary : DispatchProxy
{
    /// <summary>
    /// Gets or sets a value indicating whether ids are case-sensitive: Jellyfin's <c>EnableCaseSensitiveItemIds</c>,
    /// true by default in 12.3. When false, <c>GetNewItemId</c> lowercases the key first.
    /// </summary>
    public bool CaseSensitiveIds { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether <c>GetNewItemId</c> throws (a broken id rule).</summary>
    public bool ThrowOnIds { get; set; }

    /// <summary>
    /// Gets or sets a key whose first <c>GetNewItemId</c> request is held: <see cref="Holding"/> is set, then the call waits
    /// for <see cref="Resume"/>. Later requests for the key pass at once. Lets a test stop a thread at a known point.
    /// </summary>
    public string? HoldKey { get; set; }

    public ManualResetEventSlim Holding { get; } = new();

    public ManualResetEventSlim Resume { get; } = new();

    private int _held;

    public Dictionary<Guid, BaseItem> Items { get; } = new();

    public List<BaseItem> ListedItems { get; } = new();

    public List<(string Key, Type Type)> IdRequests { get; } = new();

    public List<BaseItem> Updated { get; } = new();

    public List<InternalItemsQuery> Queries { get; } = new();

    /// <summary>Gets the external ids whose <c>UpdateItemAsync</c> fails (a database error).</summary>
    public HashSet<string> FailUpdates { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether <c>UpdateItemAsync</c> answers a cancelled token with a cancelled task, as
    /// the real repository's database calls do.
    /// </summary>
    public bool HonourCancellation { get; set; }

    /// <summary>Gets or sets what every item query calls first, with the query's external id (it may throw).</summary>
    public Action<string?>? OnQuery { get; set; }

    /// <summary>Gets or sets what every <c>GetItemById</c> calls first (it may throw: a library lock).</summary>
    public Action<Guid>? OnGetById { get; set; }

    public ILibraryManager Manager => (ILibraryManager)(object)this;

    public static FakeLibrary Create() => (FakeLibrary)(object)Create<ILibraryManager, FakeLibrary>();

    /// <summary>
    /// <c>LibraryManager.GetNewItemIdInternal</c> (12.3) without the program-data path normalization, which a channel
    /// key never hits: lowercase unless case-sensitive, prefix the type's full name, MD5.
    /// </summary>
    public Guid IdFor(string key, Type type)
        => (type.FullName + (CaseSensitiveIds ? key : key.ToLowerInvariant())).GetMD5();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var parameters = targetMethod.GetParameters();
        switch (targetMethod.Name)
        {
            case nameof(ILibraryManager.GetNewItemId) when ThrowOnIds:
                throw new InvalidOperationException("id rule unavailable");
            case nameof(ILibraryManager.GetNewItemId):
                var key = (string)args![0]!;
                if (key == HoldKey && Interlocked.Exchange(ref _held, 1) == 0)
                {
                    Holding.Set();
                    Resume.Wait(TimeSpan.FromSeconds(30));
                }

                var type = (Type)args[1]!;
                IdRequests.Add((key, type));
                return IdFor(key, type);
            case nameof(ILibraryManager.GetItemById) when !targetMethod.IsGenericMethod && parameters.Length == 1:
                OnGetById?.Invoke((Guid)args![0]!);
                return Items.TryGetValue((Guid)args![0]!, out var item) ? item : null;
            case nameof(ILibraryManager.GetItemList) when parameters.Length == 1:
                // The repository compares ExternalId with == (BaseItemRepository.TranslateQuery, 12.3).
                // Locked: since card L4 the poller looks items up from several station fetches at once.
                var query = (InternalItemsQuery)args![0]!;
                OnQuery?.Invoke(query.ExternalId);
                lock (Queries)
                {
                    Queries.Add(query);
                    return ListedItems.Where(i => string.IsNullOrEmpty(query.ExternalId) || string.Equals(i.ExternalId, query.ExternalId, StringComparison.Ordinal)).ToList();
                }
            case nameof(ILibraryManager.UpdateItemAsync) when parameters.Length == 4:
                var updated = (BaseItem)args![0]!;
                if (HonourCancellation && args[3] is CancellationToken { IsCancellationRequested: true } cancelled)
                {
                    return Task.FromCanceled(cancelled);
                }

                if (updated.ExternalId is { } externalId && FailUpdates.Contains(externalId))
                {
                    return Task.FromException(new InvalidOperationException("database is locked"));
                }

                Updated.Add(updated);
                return Task.CompletedTask;
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}
