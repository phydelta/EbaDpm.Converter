using System.ComponentModel;
using Spectre.Console.Cli;

namespace EbaDpm.Converter.Cli;

/// <summary>
/// Every option of the command line. There is a single default command and the mode is chosen by
/// the flag present, exactly as before the move to Spectre.Console.Cli: <c>--schema-only</c>,
/// <c>--list-taxonomies</c>, <c>--validate</c>, <c>--extract-layouts</c>, and a conversion when none
/// of them is given. Which options belong to which mode, and which are required, is checked by
/// <see cref="CliRunner"/> (a flat settings class cannot express it), with the same messages as
/// the hand-written parser had. The descriptions below are the help text.
/// </summary>
public sealed class ConverterSettings : CommandSettings
{
    // ---- Convert ------------------------------------------------------------------------------

    [CommandOption("--source <PATH>")]
    [Description("Convert and --list-taxonomies: the Access DPM database (.accdb), DPM 1.0 or DPM 2.0 (detected automatically). Opened read-only. With --validate, for a native output only: the Access database it was converted from; it enables the checks against the source (completeness, row counts, content, schema drift).")]
    public string? Source { get; init; }

    [CommandOption("--output <PATH>")]
    [Description("Convert and --schema-only: the SQLite database to create. With --target native: the file path. DPM 2.0 without --release: a DIRECTORY (created if missing) that receives one file dpm-<release code>.db per release.")]
    public string? Output { get; init; }

    [CommandOption("--taxonomies <LIST>")]
    [Description("DPM 1.0 selector: comma-separated TaxonomyCode values as stored in mTaxonomy.TaxonomyCode, e.g. \"COREP 3.2,IF 3.2\". Exactly ONE selector is required; an error with a DPM 2.0 source.")]
    public string? Taxonomies { get; init; }

    [CommandOption("--taxonomykeys <LIST>")]
    [Description("DPM 1.0 selector: comma-separated TaxonomyKey values (a derived identifier, not a stored value), e.g. corep/its-005-2020/2022-03-01 (up to 3.x) or sbp/4.0 (from 4.0).")]
    public string? TaxonomyKeys { get; init; }

    [CommandOption("--releases <LIST>")]
    [Description("DPM 1.0 selector: comma-separated DpmPackageCode values, e.g. 4.0,4.1.")]
    public string? Releases { get; init; }

    [CommandOption("--all")]
    [Description("DPM 1.0 selector: all taxonomies.")]
    public bool All { get; init; }

    [CommandOption("--release <CODE>")]
    [Description("DPM 2.0 only: convert only the release with that Release.Code (e.g. 4.3); --output is then the file path. A release in which no exportable module version starts is an error. An error with a DPM 1.0 source and with --target native. Also narrows --list-taxonomies.")]
    public string? Release { get; init; }

    [CommandOption("--target <TARGET>")]
    [Description("Convert and --schema-only: the output schema. distribution (default): the 45-table DPM distribution schema. native: the DPM 2.0 Access database table for table (the same tables, columns, rows and IDs, every release in one file); DPM 2.0 sources that contain release 4.4 or later only; --output is a file path; --release and the DPM 1.0 selectors do not apply. With --schema-only: the empty baseline (4.4) schema.")]
    public string? Target { get; init; }

    [CommandOption("--overwrite")]
    [Description("Replace output files that already exist (convert, --schema-only, --extract-layouts). Without it an existing output file is an error and, for DPM 2.0 without --release, nothing is written.")]
    public bool Overwrite { get; init; }

    [CommandOption("--report <PATH>")]
    [Description("--validate: write the full validation report as JSON. In a conversion it is accepted for compatibility and has no effect.")]
    public string? Report { get; init; }

    [CommandOption("--verbose")]
    [Description("Conversion only. Accepted for compatibility; has no additional effect.")]
    public bool Verbose { get; init; }

    // ---- Other modes ----------------------------------------------------------------------------

    [CommandOption("--list-taxonomies")]
    [Description("Mode: list the taxonomies of --source with their TaxonomyKey and TaxonomyCode, to know what to pass to the DPM 1.0 selectors. Takes --source and optionally --release.")]
    public bool ListTaxonomies { get; init; }

    [CommandOption("--schema-only")]
    [Description("Mode: create an empty database with the 45 tables of the distribution schema in --output (with --target native: the empty native schema, baseline 4.4). No Access database is needed.")]
    public bool SchemaOnly { get; init; }

    [CommandOption("--validate <PATH>")]
    [Description("Mode: validate the generated SQLite database at PATH (never modified). A native DPM 2.0 output (--target native) is detected and has its own rule set: plane A (relationships, schema; with --source also completeness, row counts, content and the schema drift report), no plane B. A 45-table output: plane A (internal invariants) always runs; --reference adds plane B. --layouts adds plane C to either. Exit code 3 on critical differences.")]
    public string? ValidatePath { get; init; }

    [CommandOption("--reference <PATH>")]
    [Description("--validate: a reference SQLite export; adds plane B, the semantic diff against it (reported, does NOT decide the exit code).")]
    public string? Reference { get; init; }

    [CommandOption("--layouts <PATH>")]
    [Description("--validate: a layout repository produced by --extract-layouts; adds plane C, the signature-by-signature comparison against the Annotated Table Layouts. Independent of --reference. Without it plane C does not run and the report says so.")]
    public string? Layouts { get; init; }

    [CommandOption("--extract-layouts <DIRECTORY>")]
    [Description("Mode: dump the Annotated Table Layouts (.xlsx) found recursively under DIRECTORY into a queryable SQLite repository. Requires --out and --dictionary.")]
    public string? ExtractLayouts { get; init; }

    [CommandOption("--out <PATH>")]
    [Description("--extract-layouts: the layout repository (SQLite) to create.")]
    public string? Out { get; init; }

    [CommandOption("--dictionary <PATH>")]
    [Description("--extract-layouts: a database already converted by this tool, REQUIRED to classify single-term \"(code)\" cells as metric or key dimension and to validate \"(domain:member)\". Either output: the 45-table distribution schema (mDimension, mDomain, mMember) or a native DPM 2.0 file (--target native; every release of the file is read, and the module framework comes from Module and Framework). Its path and SHA-256 are recorded in LayoutExtraction.")]
    public string? Dictionary { get; init; }

    // ---- Output ---------------------------------------------------------------------------------

    [CommandOption("--plain")]
    [Description("Force plain output: no colours, no live progress, tables without borders. It is the default when output is redirected; NO_COLOR is also honoured.")]
    public bool Plain { get; init; }

    /// <summary>Console and diagnostics of this run; set by the interceptor, not by the command line.</summary>
    internal CliContext? Context { get; set; }

    /// <summary>
    /// The options present on the command line, by name, in a fixed order. Used to reject, in each
    /// mode, the options that belong to another one.
    /// </summary>
    internal IReadOnlyList<string> ProvidedOptions()
    {
        var provided = new List<string>();
        void Add(bool present, string name)
        {
            if (present)
            {
                provided.Add(name);
            }
        }

        Add(Source is not null, "--source");
        Add(Output is not null, "--output");
        Add(Taxonomies is not null, "--taxonomies");
        Add(TaxonomyKeys is not null, "--taxonomykeys");
        Add(Releases is not null, "--releases");
        Add(All, "--all");
        Add(Release is not null, "--release");
        Add(Target is not null, "--target");
        Add(Overwrite, "--overwrite");
        Add(Report is not null, "--report");
        Add(Verbose, "--verbose");
        Add(ListTaxonomies, "--list-taxonomies");
        Add(SchemaOnly, "--schema-only");
        Add(ValidatePath is not null, "--validate");
        Add(Reference is not null, "--reference");
        Add(Layouts is not null, "--layouts");
        Add(ExtractLayouts is not null, "--extract-layouts");
        Add(Out is not null, "--out");
        Add(Dictionary is not null, "--dictionary");
        Add(Plain, "--plain");
        return provided;
    }

    /// <summary>The tokens Spectre could not match with an option, as typed.</summary>
    internal IReadOnlyList<string> UnknownTokens { get; set; } = [];
}
