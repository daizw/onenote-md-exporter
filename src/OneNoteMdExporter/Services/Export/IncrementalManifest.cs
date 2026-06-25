using alxnbl.OneNoteMdExporter.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace alxnbl.OneNoteMdExporter.Services.Export
{
    /// <summary>
    /// One entry per exported page, persisted in the incremental manifest.
    /// </summary>
    public class ManifestPageEntry
    {
        /// <summary>OneNote's lastModifiedTime for the page at the moment it was exported.</summary>
        public DateTime OneNoteLastModified { get; set; }

        /// <summary>Path (relative to the notebook export folder) of the generated .md file.</summary>
        public string OutputPath { get; set; }

        /// <summary>"ok" if the page exported successfully, "error" if it failed (so it is retried next run).</summary>
        public string Status { get; set; }

        /// <summary>UTC timestamp of the last export attempt for this page.</summary>
        public DateTime LastExportedAt { get; set; }
    }

    /// <summary>
    /// State file written next to a notebook export that records, per OneNote page id,
    /// the OneNote last-modified timestamp at the time the page was last converted.
    ///
    /// Root purpose: let a re-run skip pages that have not changed in OneNote since the
    /// previous run, and resume a batch that was interrupted, without re-converting everything.
    ///
    /// The manifest is keyed by the stable OneNote page id (a GUID-bearing string) rather than
    /// by title or output path, so renames and moves do not defeat the skip logic.
    /// </summary>
    public class IncrementalManifest
    {
        public const string FileName = "onenote-export-manifest.json";

        public int SchemaVersion { get; set; } = 1;

        /// <summary>UTC timestamp of the run that last wrote this manifest.</summary>
        public DateTime ExportedAt { get; set; }

        /// <summary>OneNote notebook id this manifest belongs to (sanity check on load).</summary>
        public string NotebookId { get; set; }

        /// <summary>Map of OneNote page id -> last successful export metadata.</summary>
        public Dictionary<string, ManifestPageEntry> Pages { get; set; } = new();

        [JsonIgnore]
        private string _path;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Load the manifest stored in <paramref name="notebookExportFolder"/>, or return a fresh
        /// empty manifest if none exists (or the existing one is unreadable/incompatible).
        /// Never throws on a corrupt manifest: a bad manifest must not abort an export, it just
        /// means nothing can be skipped this run. The failure is logged loudly.
        /// </summary>
        public static IncrementalManifest LoadOrCreate(string notebookExportFolder, string notebookId)
        {
            var path = Path.Combine(notebookExportFolder, FileName);

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var manifest = JsonSerializer.Deserialize<IncrementalManifest>(json, _jsonOptions);

                    if (manifest != null && manifest.SchemaVersion == 1)
                    {
                        manifest._path = path;
                        manifest.Pages ??= new();

                        if (!string.IsNullOrEmpty(manifest.NotebookId)
                            && !string.IsNullOrEmpty(notebookId)
                            && !string.Equals(manifest.NotebookId, notebookId, StringComparison.OrdinalIgnoreCase))
                        {
                            // Stale manifest from a different notebook reusing the same folder name.
                            // Fail loud and start clean rather than silently skipping the wrong pages.
                            Log.Warning("Incremental: manifest at {Path} belongs to notebook {Old} but exporting {New}; ignoring it and re-exporting all pages.",
                                path, manifest.NotebookId, notebookId);
                            return new IncrementalManifest { NotebookId = notebookId, _path = path };
                        }

                        Log.Information("Incremental: loaded manifest with {Count} known page(s) from {Path}.", manifest.Pages.Count, path);
                        return manifest;
                    }

                    Log.Warning("Incremental: manifest at {Path} has unsupported schema version; ignoring it.", path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Incremental: failed to read manifest at {Path}; starting with an empty manifest.", path);
                }
            }

            return new IncrementalManifest { NotebookId = notebookId, _path = path };
        }

        /// <summary>
        /// True when the page exists in the manifest, was last exported successfully, and its
        /// OneNote last-modified timestamp is unchanged since then, AND the previously generated
        /// output file still exists on disk. Any of those failing => the page must be re-exported.
        /// </summary>
        public bool CanSkip(Page page, string currentOutputAbsolutePath)
        {
            if (page?.OneNoteId == null || !Pages.TryGetValue(page.OneNoteId, out var entry))
                return false;

            if (!string.Equals(entry.Status, "ok", StringComparison.OrdinalIgnoreCase))
                return false; // last run errored on this page -> retry it

            // OneNote timestamps are second-resolution; compare with a small tolerance.
            if (Math.Abs((entry.OneNoteLastModified - page.LastModificationDate).TotalSeconds) > 1)
                return false; // page changed in OneNote -> re-export

            // The recorded output file must still be present, else re-export to regenerate it.
            if (!File.Exists(currentOutputAbsolutePath))
                return false;

            return true;
        }

        /// <summary>
        /// Record the outcome of a page export. <paramref name="outputRelativePath"/> is stored
        /// for diagnostics; <paramref name="success"/> drives the skip decision on the next run.
        /// </summary>
        public void RecordPage(Page page, string outputRelativePath, bool success)
        {
            if (page?.OneNoteId == null)
                return;

            Pages[page.OneNoteId] = new ManifestPageEntry
            {
                OneNoteLastModified = page.LastModificationDate,
                OutputPath = outputRelativePath,
                Status = success ? "ok" : "error",
                LastExportedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Persist the manifest to disk. Called after every page so an interrupted run still
        /// records the progress it made. Writes via a temp file + move to avoid a half-written
        /// manifest if the process dies mid-write. Never throws: a manifest write failure must
        /// not abort the export, but it is logged loudly.
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(_path))
                return;

            try
            {
                ExportedAt = DateTime.UtcNow;
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                var json = JsonSerializer.Serialize(this, _jsonOptions);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);

                // Atomic-ish replace.
                if (File.Exists(_path))
                    File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Incremental: failed to write manifest at {Path}; progress for this run may not be resumable.", _path);
            }
        }
    }
}
