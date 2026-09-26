using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;
using LinkedChildType = Jellyfin.Database.Implementations.Entities.LinkedChildType;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers the item queries no grouping key applies to. Every filter is a predicate over the item's own row -
/// an EXISTS or a membership test over a sub-select, never a join that multiplies it - so an item matching
/// through several related rows (two credits of one name, two genre values, ids of two providers, two
/// subtitle tracks) is listed once, and the query does not ask the database to deduplicate whole rows.
/// </summary>
public sealed class BaseItemRepositoryDuplicateItemTests : DbTestFixture
{
    private const string PersonName = "Some Person";
    private const string GenreName = "Some Genre";
    private const string TagName = "Some Tag";
    private const string FirstProviderName = "Tmdb";
    private const string SecondProviderName = "Imdb";

    private readonly CommandRecorder _recorder;
    private readonly BaseItemRepository _repository;
    private readonly ItemTypeLookup _itemTypeLookup = new();
    private readonly Dictionary<Guid, string> _names = [];

    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _shelf = Guid.NewGuid();
    private readonly Guid _collection = Guid.NewGuid();
    private readonly Guid _movie = Guid.NewGuid();
    private readonly Guid _otherMovie = Guid.NewGuid();
    private readonly Guid _personItem = Guid.NewGuid();
    private readonly Guid _genreItem = Guid.NewGuid();

    public BaseItemRepositoryDuplicateItemTests()
        : this(new CommandRecorder())
    {
    }

    private BaseItemRepositoryDuplicateItemTests(CommandRecorder recorder)
        : base(recorder)
    {
        _recorder = recorder;
        using (var context = CreateDbContext())
        {
            Seed(context);
        }

        _repository = CreateBaseItemRepository(_itemTypeLookup);
    }

    /// <summary>
    /// The filters under test. Each one matches the movie through more than one related row.
    /// </summary>
    public enum FilterCase
    {
        /// <summary>
        /// Every seeded type, so nothing may be dropped either.
        /// </summary>
        ItemTypes,

        /// <summary>
        /// A person id, resolved to every credit carrying that person's name.
        /// </summary>
        PersonId,

        /// <summary>
        /// A person name, matched against the item's credits.
        /// </summary>
        PersonName,

        /// <summary>
        /// A genre name, matched against the item's values.
        /// </summary>
        GenreName,

        /// <summary>
        /// A genre id, resolved to every value cleaning to that genre's name.
        /// </summary>
        GenreId,

        /// <summary>
        /// A tag name, matched against the item's values.
        /// </summary>
        TagName,

        /// <summary>
        /// Two providers, both of which the item carries an id for.
        /// </summary>
        ProviderIds,

        /// <summary>
        /// Two ancestors, matched against the item's ancestor rows.
        /// </summary>
        AncestorIds,

        /// <summary>
        /// A subtitle language, matched against the item's streams and against the folders above it.
        /// </summary>
        SubtitleLanguage,

        /// <summary>
        /// Box set collapsing, which stands one box set in for both of its members.
        /// </summary>
        CollapsedBoxSet
    }

    [Theory]
    [InlineData(FilterCase.ItemTypes, "Collection,Extra shelf,Library,Movie,Other,Some Genre,Some Person")]
    [InlineData(FilterCase.PersonId, "Movie")]
    [InlineData(FilterCase.PersonName, "Movie")]
    [InlineData(FilterCase.GenreName, "Movie")]
    [InlineData(FilterCase.GenreId, "Movie")]
    [InlineData(FilterCase.TagName, "Movie")]
    [InlineData(FilterCase.ProviderIds, "Movie")]
    [InlineData(FilterCase.AncestorIds, "Movie,Other")]
    [InlineData(FilterCase.SubtitleLanguage, "Collection,Extra shelf,Library,Movie")]
    [InlineData(FilterCase.CollapsedBoxSet, "Collection")]
    public void GetItemIdsList_WithAFilterMatchingSeveralRelatedRows_ListsEachItemOnce(FilterCase filterCase, string expected)
    {
        var ids = _repository.GetItemIdsList(QueryFor(filterCase));

        Assert.Equal(expected.Split(','), NamesOf(ids));
    }

    [Theory]
    [InlineData(FilterCase.ItemTypes, 7)]
    [InlineData(FilterCase.PersonId, 1)]
    [InlineData(FilterCase.PersonName, 1)]
    [InlineData(FilterCase.GenreName, 1)]
    [InlineData(FilterCase.GenreId, 1)]
    [InlineData(FilterCase.TagName, 1)]
    [InlineData(FilterCase.ProviderIds, 1)]
    [InlineData(FilterCase.AncestorIds, 2)]
    [InlineData(FilterCase.SubtitleLanguage, 4)]
    [InlineData(FilterCase.CollapsedBoxSet, 1)]
    public void GetItems_WithAFilterMatchingSeveralRelatedRows_CountsEachItemOnce(FilterCase filterCase, int expected)
    {
        // The total a client pages against is counted on the same query the listing pages through.
        var query = QueryFor(filterCase);
        query.Limit = 1;
        query.EnableTotalRecordCount = true;

        Assert.Equal(expected, _repository.GetItems(query).TotalRecordCount);
    }

    [Theory]
    [InlineData(FilterCase.ItemTypes, "Collection,Extra shelf,Library,Movie,Other,Some Genre,Some Person")]
    [InlineData(FilterCase.PersonId, "Movie")]
    [InlineData(FilterCase.SubtitleLanguage, "Collection,Extra shelf,Library,Movie")]
    [InlineData(FilterCase.CollapsedBoxSet, "Collection")]
    public void GetItemList_WithItemsCarryingSeveralOwnedRows_ReturnsEachItemOnce(FilterCase filterCase, string expected)
    {
        // The images, provider ids, linked children and user data rows the query brings back alongside the
        // item are joined onto it, so the movie arrives spread over several rows of the result set.
        var items = _repository.GetItemList(QueryFor(filterCase));

        Assert.Equal(expected.Split(','), NamesOf(items.Select(i => i.Id).ToArray()));
    }

    [Fact]
    public void GetItemIdsList_ForAQueryWithoutAGroupingKey_DoesNotDeduplicateWholeRows()
    {
        _recorder.Commands.Clear();

        _repository.GetItemIdsList(StoredColumnsQuery());

        var command = Assert.Single(_recorder.Commands);
        Assert.DoesNotContain("SELECT DISTINCT", command, StringComparison.Ordinal);
    }

    [Fact]
    public void GetItemList_ForAQueryWithoutAGroupingKey_DoesNotDeduplicateWholeRows()
    {
        _recorder.Commands.Clear();

        _repository.GetItemList(StoredColumnsQuery());

        var command = Assert.Single(_recorder.Commands);
        Assert.DoesNotContain("SELECT DISTINCT", command, StringComparison.Ordinal);
    }

    private static InternalItemsQuery StoredColumnsQuery()
        => new()
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            DtoOptions = DtoOptions.StoredColumnsOnly
        };

    private InternalItemsQuery QueryFor(FilterCase filterCase)
        => filterCase switch
        {
            FilterCase.ItemTypes => new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Folder, BaseItemKind.BoxSet, BaseItemKind.Person, BaseItemKind.Genre]
            },
            FilterCase.PersonId => new InternalItemsQuery { PersonIds = [_personItem] },
            FilterCase.PersonName => new InternalItemsQuery { Person = PersonName },
            FilterCase.GenreName => new InternalItemsQuery { Genres = [GenreName] },
            FilterCase.GenreId => new InternalItemsQuery { GenreIds = [_genreItem] },
            FilterCase.TagName => new InternalItemsQuery { Tags = [TagName] },
            FilterCase.ProviderIds => new InternalItemsQuery
            {
                HasAnyProviderIds = new Dictionary<string, string[]> { [FirstProviderName] = [], [SecondProviderName] = [] }
            },
            FilterCase.AncestorIds => new InternalItemsQuery { AncestorIds = [_library, _shelf] },
            FilterCase.SubtitleLanguage => new InternalItemsQuery { SubtitleLanguages = ["ger"] },
            FilterCase.CollapsedBoxSet => new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.BoxSet],
                CollapseBoxSetItems = true
            },
            _ => throw new ArgumentOutOfRangeException(nameof(filterCase))
        };

    private string[] NamesOf(IReadOnlyList<Guid> ids) => ids.Select(id => _names[id]).ToArray();

    private void Seed(JellyfinDbContext context)
    {
        AddFolder(context, _library, "Library", BaseItemKind.Folder);
        AddFolder(context, _shelf, "Extra shelf", BaseItemKind.Folder);
        AddFolder(context, _collection, "Collection", BaseItemKind.BoxSet);
        AddItem(context, _movie, "Movie", BaseItemKind.Movie);
        AddItem(context, _otherMovie, "Other", BaseItemKind.Movie);
        AddItem(context, _personItem, PersonName, BaseItemKind.Person);
        AddItem(context, _genreItem, GenreName, BaseItemKind.Genre);

        // The movie sits under two folders, so the ancestor filter reaches it through two rows.
        AddAncestor(context, _movie, _library);
        AddAncestor(context, _movie, _shelf);
        AddAncestor(context, _otherMovie, _library);

        // Both movies hang in the same box set, which collapsing stands in for once.
        AddLinkedChild(context, _collection, _movie, 0);
        AddLinkedChild(context, _collection, _otherMovie, 1);

        // Two credits of one name: the person id resolves to both people rows, and both map to the movie.
        foreach (var role in new[] { "Director", "Writer" })
        {
            var person = new People { Id = Guid.NewGuid(), Name = PersonName, PersonType = role };
            context.Peoples.Add(person);
            context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
            {
                ItemId = _movie,
                PeopleId = person.Id,
                Role = role,
                Item = null!,
                People = null!
            });
        }

        // Two values of one kind cleaning to the same name, as a rescan that renamed the case leaves behind.
        AddItemValue(context, _movie, ItemValueType.Genre, GenreName);
        AddItemValue(context, _movie, ItemValueType.Genre, GenreName.ToUpperInvariant());
        AddItemValue(context, _movie, ItemValueType.Tags, TagName);
        AddItemValue(context, _movie, ItemValueType.Tags, TagName.ToUpperInvariant());

        // Ids of two providers, two subtitle tracks of one language, and the owned rows an item query
        // brings back alongside the item itself.
        context.BaseItemProviders.Add(new BaseItemProvider { ItemId = _movie, ProviderId = FirstProviderName, ProviderValue = "1", Item = null! });
        context.BaseItemProviders.Add(new BaseItemProvider { ItemId = _movie, ProviderId = SecondProviderName, ProviderValue = "tt1", Item = null! });
        context.MediaStreamInfos.Add(new MediaStreamInfo { ItemId = _movie, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Subtitle, Language = "ger", Item = null! });
        context.MediaStreamInfos.Add(new MediaStreamInfo { ItemId = _movie, StreamIndex = 1, StreamType = MediaStreamTypeEntity.Subtitle, Language = "ger", Item = null! });
        context.BaseItemImageInfos.Add(new BaseItemImageInfo { Id = Guid.NewGuid(), ItemId = _movie, Path = "/primary.jpg", ImageType = ImageInfoImageType.Primary, Blurhash = null, Item = null! });
        context.BaseItemImageInfos.Add(new BaseItemImageInfo { Id = Guid.NewGuid(), ItemId = _movie, Path = "/backdrop.jpg", ImageType = ImageInfoImageType.Backdrop, Blurhash = null, Item = null! });

        foreach (var name in new[] { "first", "second" })
        {
            var user = new User(name, "auth-provider", "reset-provider");
            context.Users.Add(user);
            context.UserData.Add(new UserData
            {
                ItemId = _movie,
                UserId = user.Id,
                CustomDataKey = _movie.ToString("N"),
                Played = true,
                Item = null!,
                User = null!
            });
        }

        context.SaveChanges();
    }

    private void AddFolder(JellyfinDbContext context, Guid id, string name, BaseItemKind kind)
    {
        var entity = NewItem(id, name, kind);
        entity.IsFolder = true;
        context.BaseItems.Add(entity);
    }

    private void AddItem(JellyfinDbContext context, Guid id, string name, BaseItemKind kind)
        => context.BaseItems.Add(NewItem(id, name, kind));

    private BaseItemEntity NewItem(Guid id, string name, BaseItemKind kind)
    {
        _names[id] = name;
        return new BaseItemEntity
        {
            Id = id,
            Type = _itemTypeLookup.BaseItemKindNames[kind],
            Name = name,
            SortName = name,
            CleanName = name.GetCleanValue(),
            PresentationUniqueKey = id.ToString("N")
        };
    }

    private static void AddAncestor(JellyfinDbContext context, Guid itemId, Guid parentId)
        => context.AncestorIds.Add(new AncestorId { ItemId = itemId, ParentItemId = parentId, Item = null!, ParentItem = null! });

    private static void AddLinkedChild(JellyfinDbContext context, Guid parentId, Guid childId, int sortOrder)
        => context.LinkedChildren.Add(new LinkedChildEntity
        {
            ParentId = parentId,
            ChildId = childId,
            ChildType = LinkedChildType.Manual,
            SortOrder = sortOrder
        });

    private static void AddItemValue(JellyfinDbContext context, Guid itemId, ItemValueType type, string value)
    {
        var itemValue = new ItemValue
        {
            ItemValueId = Guid.NewGuid(),
            Type = type,
            Value = value,
            CleanValue = value.GetCleanValue()
        };

        context.ItemValues.Add(itemValue);
        context.ItemValuesMap.Add(new ItemValueMap
        {
            ItemId = itemId,
            ItemValueId = itemValue.ItemValueId,
            Item = null!,
            ItemValue = null!
        });
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }
    }
}
