using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// Manages the storage and retrieval of display preferences through Entity Framework.
/// </summary>
public sealed class DisplayPreferencesManager : IDisplayPreferencesManager
{
    /// <summary>
    /// How often a write may start over because another writer stored the rows it was about to insert. The second
    /// attempt reads those rows; a third is only reached when they were replaced again in between.
    /// </summary>
    private const int MaxWriteAttempts = 3;

    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;
    private readonly IJellyfinDatabaseProvider _databaseProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DisplayPreferencesManager"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    /// <param name="databaseProvider">The database provider, which tells this one what a failure the database reported means.</param>
    public DisplayPreferencesManager(IDbContextFactory<JellyfinDbContext> dbContextFactory, IJellyfinDatabaseProvider databaseProvider)
    {
        _dbContextFactory = dbContextFactory;
        _databaseProvider = databaseProvider;
    }

    /// <inheritdoc />
    public DisplayPreferences GetDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        // The lookup is a read, so it runs outside the write lock that queues the insert behind other writes in
        // this process. Another caller here, or a second server on the same database, can store the row in
        // between, and the insert then fails on the unique index over (UserId, ItemId, Client). The row it meets
        // holds the defaults this call was about to write, so reading it back is the answer the caller asked for.
        for (var attempt = 1; ; attempt++)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var prefs = dbContext.DisplayPreferences
                .Include(pref => pref.HomeSections)
                .FirstOrDefault(pref =>
                    pref.UserId.Equals(userId) && pref.Client == client && pref.ItemId.Equals(itemId));

            if (prefs is not null)
            {
                return prefs;
            }

            prefs = new DisplayPreferences(userId, itemId, client);
            dbContext.DisplayPreferences.Add(prefs);

            try
            {
                dbContext.SaveChanges();
            }
            catch (Exception exception) when (attempt < MaxWriteAttempts && _databaseProvider.ClassifyException(exception) == DatabaseErrorKind.UniqueViolation)
            {
                // The rejected insert wrote nothing, so the next attempt's lookup reads the stored row.
                continue;
            }

            return prefs;
        }
    }

    /// <inheritdoc />
    public ItemDisplayPreferences GetItemDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var prefs = dbContext.ItemDisplayPreferences
            .FirstOrDefault(pref => pref.UserId.Equals(userId) && pref.ItemId.Equals(itemId) && pref.Client == client);

        if (prefs is null)
        {
            // The row is stored under the item it belongs to, so the next call for that item reads this one back.
            // Nothing in the schema says there is only one, so a writer that stores it at the same time as this
            // call adds a second row rather than failing, and the two are read back one at a time until one of
            // them is updated; making the pair impossible needs a unique index, which cannot be created while
            // databases carry the rows an earlier build stored under no item at all.
            prefs = new ItemDisplayPreferences(userId, itemId, client);
            dbContext.ItemDisplayPreferences.Add(prefs);
            dbContext.SaveChanges();
        }

        return prefs;
    }

    /// <inheritdoc />
    public IList<ItemDisplayPreferences> ListItemDisplayPreferences(Guid userId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.ItemDisplayPreferences
            .Where(prefs => prefs.UserId.Equals(userId) && !prefs.ItemId.Equals(default) && prefs.Client == client)
            .ToList();
    }

    /// <inheritdoc />
    public Dictionary<string, string?> ListCustomItemDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.CustomItemDisplayPreferences
            .Where(prefs => prefs.UserId.Equals(userId)
                            && prefs.ItemId.Equals(itemId)
                            && prefs.Client == client)
            .ToDictionary(prefs => prefs.Key, prefs => prefs.Value);
    }

    /// <inheritdoc />
    public void SetCustomItemDisplayPreferences(Guid userId, Guid itemId, string client, Dictionary<string, string?> customPreferences)
    {
        // Replacing the preferences takes a delete and an insert, and both belong to one transaction: without it
        // a failed insert leaves the preferences deleted, and the write lock lets another writer in between the
        // two. What the transaction cannot keep out is a second server on the same database, which can store a
        // key this call is about to insert after this call's delete has looked for it. The insert then fails on
        // the unique index over (UserId, ItemId, Client, Key). This call was given the whole set of preferences,
        // so writing it again replaces whatever is stored with that same set.
        for (var attempt = 1; ; attempt++)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            using var transaction = dbContext.Database.BeginTransaction();

            dbContext.CustomItemDisplayPreferences.Where(prefs => prefs.UserId.Equals(userId)
                                && prefs.ItemId.Equals(itemId)
                                && prefs.Client == client)
                                .ExecuteDelete();

            foreach (var (key, value) in customPreferences)
            {
                dbContext.CustomItemDisplayPreferences
                    .Add(new CustomItemDisplayPreferences(userId, itemId, client, key.SanitizeForDatabase(), value.SanitizeForDatabase()));
            }

            try
            {
                dbContext.SaveChanges();
            }
            catch (Exception exception) when (attempt < MaxWriteAttempts && _databaseProvider.ClassifyException(exception) == DatabaseErrorKind.UniqueViolation)
            {
                // The transaction rolls back with this attempt, so it deleted nothing either, and the next one
                // starts over: its delete finds the rows that were stored in the meantime and removes them too.
                continue;
            }

            transaction.Commit();
            return;
        }
    }

    /// <inheritdoc/>
    public void UpdateDisplayPreferences(DisplayPreferences displayPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        dbContext.DisplayPreferences.Attach(displayPreferences).State = EntityState.Modified;
        dbContext.SaveChanges();
    }

    /// <inheritdoc/>
    public void UpdateItemDisplayPreferences(ItemDisplayPreferences itemDisplayPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        dbContext.ItemDisplayPreferences.Attach(itemDisplayPreferences).State = EntityState.Modified;
        dbContext.SaveChanges();
    }
}
