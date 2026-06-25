using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace alxnbl.OneNoteMdExporter.Models
{
    public class NotebookExportResult
    {
        /// <summary>
        /// Contains the errorCode of the error that cause a crash during the export. Null if export ended correctly.
        /// </summary>
        public string NoteBookExportErrorCode { get; set; }

        /// <summary>
        /// Contains the error message the an error that cause a crash during the export. Null if export ended correctly.
        /// </summary>
        public string NoteBookExportErrorMessage { get; set; }

        /// <summary>
        /// Number of pages that fail to be exported
        /// </summary>
        public int PagesOnError { get; set; } = 0;

        /// <summary>
        /// Number of pages skipped because they were unchanged since the last incremental export.
        /// </summary>
        public int PagesSkipped { get; set; } = 0;

        /// <summary>
        /// Number of image references in the exported markdown whose target file is missing on disk
        /// (a broken image). Surfaced so silent image loss - a known OneNote-sync failure mode -
        /// produces an observable signal instead of a quietly broken export.
        /// </summary>
        public int BrokenImageCount { get; set; } = 0;
    }
}
