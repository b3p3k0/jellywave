using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// A minimal <see cref="ILibraryManager"/> (through <see cref="DispatchProxy"/>, the interface is large): item ids
/// derived the way Jellyfin 12.3's <c>LibraryManager.GetNewItemId</c> derives them, items by id, one item list for
/// the live-item query, and a record of every <c>UpdateItemAsync</c>. Anything else throws.
/// </summary>
public class FakeLibrary : DispatchProxy
{
    /// <summary>
    /// Gets or sets a value indicating whether ids are case-sensitive: Jellyfin's <c>EnableCaseSensitiveItemIds</c>,
    /// true by default in 12.3. When false, <c>GetNewItemId</c> lowercases the key first.
    /// </summary>
    public bool CaseSensitiveIds { get; set; } = true;

    public Dictionary<Guid, BaseItem> Items { get; } = new();

    public List<BaseItem> ListedItems { get; } = new();

    public List<(string Key, Type Type)> IdRequests { get; } = new();

    public List<BaseItem> Updated { get; } = new();

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
            case nameof(ILibraryManager.GetNewItemId):
                var key = (string)args![0]!;
                var type = (Type)args[1]!;
                IdRequests.Add((key, type));
                return IdFor(key, type);
            case nameof(ILibraryManager.GetItemById) when !targetMethod.IsGenericMethod && parameters.Length == 1:
                return Items.TryGetValue((Guid)args![0]!, out var item) ? item : null;
            case nameof(ILibraryManager.GetItemList) when parameters.Length == 1:
                return ListedItems.ToList();
            case nameof(ILibraryManager.UpdateItemAsync) when parameters.Length == 4:
                Updated.Add((BaseItem)args![0]!);
                return Task.CompletedTask;
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}
