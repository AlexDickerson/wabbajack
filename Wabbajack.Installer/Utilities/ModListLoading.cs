using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Downloaders;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.DTOs;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.Compression.Zip;
using System.Text.Json;

namespace Wabbajack.Installer.Utilities
{
    public static class ModListLoading
    {
        public static async Task<ModList> LoadFromFile(DTOSerializer serializer, AbsolutePath path)
        {
            await using var fs = path.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ar = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = ar.GetEntry("modlist");
            if (entry == null)
            {
                entry = ar.GetEntry("modlist.json");
                if (entry == null)
                    throw new Exception("Invalid Wabbajack Installer");
                await using var e = entry.Open();
                return (await serializer.DeserializeAsync<ModList>(e))!;
            }

            await using (var e = entry.Open())
            {
                return (await serializer.DeserializeAsync<ModList>(e))!;
            }
        }

        public static async Task<ModList> Load(DTOSerializer dtos, DownloadDispatcher dispatcher, ModlistMetadata metadata, CancellationToken token)
        {
            var archive = new Archive
            {
                State = dispatcher.Parse(new Uri(metadata.Links.Download))!,
                Size = metadata.DownloadMetadata!.Size,
                Hash = metadata.DownloadMetadata.Hash
            };

            await using var stream = await dispatcher.ChunkedSeekableStream(archive, token);
            await using var reader = new ZipReader(stream);
            var entry = (await reader.GetFiles()).First(e => e.FileName == "modlist");
            using var ms = new MemoryStream();
            await reader.Extract(entry, ms, token);
            ms.Position = 0;
            return JsonSerializer.Deserialize<ModList>(ms, dtos.Options)!;
        }
    }
}
