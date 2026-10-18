#nullable disable

#pragma warning disable CA1724, CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Providers;

namespace MediaBrowser.Controller.Entities
{
    [Common.RequiresSourceSerialisation]
    public class AudioBook : Audio.Audio, IHasSeries, IHasLookupInfo<SongInfo>
    {
        [JsonIgnore]
        public override bool SupportsPositionTicksResume => true;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => true;

        [JsonIgnore]
        public string SeriesPresentationUniqueKey { get; set; }

        [JsonIgnore]
        public string SeriesName { get; set; }

        [JsonIgnore]
        public Guid SeriesId { get; set; }

        public string FindSeriesSortName()
        {
            return SeriesName;
        }

        public string FindSeriesName()
        {
            return SeriesName;
        }

        public string FindSeriesPresentationUniqueKey()
        {
            return SeriesPresentationUniqueKey;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            return 0;
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            // Without album and album artist tags the song key Audio puts first is little more than
            // the file name, such as "Chapter 01", so one book would reattach another book's progress.
            if (string.IsNullOrEmpty(Album) || string.IsNullOrEmpty(AlbumArtists.FirstOrDefault()))
            {
                list.RemoveAt(0);
            }

            return list;
        }

        public Guid FindSeriesId()
        {
            return SeriesId;
        }

        public override bool CanDownload()
        {
            return IsFileProtocol;
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Book;
        }
    }
}
