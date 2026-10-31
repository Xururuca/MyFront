using MyFront.Core;
using ScintillaNET;

namespace MyFront.UI;

/// <summary>A window that takes dropped files (the main window: Excel / CSV import).</summary>
internal interface IFileDropTarget
{
    DragDropEffects FileDragEffect(IDataObject? data);
    void DropFiles(IDataObject data);
}

/// <summary>
/// Scintilla configured for MariaDB/MySQL syntax highlighting. A name dragged from the navigator is inserted where it is
/// dropped; files dropped on it go to the window (import), as if dropped next to it.
/// </summary>
internal sealed class SqlEditor : Scintilla
{
    private const int ErrorIndicator = 8;

    /// <summary>Data format the navigator puts next to the text of a dragged name (the editor takes only those).</summary>
    public const string NameFormat = "MyFront.Name";

    /// <summary>What dragging a name carries: the text, and the marker that it is a name.</summary>
    public static DataObject NameData(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData(NameFormat, text);
        return data;
    }

    /// <summary>The selection before a name was dragged over the editor (put back when it leaves without a drop).</summary>
    private (int Anchor, int Caret)? _beforeDrag;

    private const string Keywords =
        "accessible add after against algorithm all alter analyze and as asc asensitive auto_increment before begin between " +
        "bigint binary bit blob bool boolean both by call cascade case change char character charset check close collate " +
        "column columns comment commit committed condition constraint continue convert create cross current current_date " +
        "current_time current_timestamp current_user cursor data database databases date datetime day day_hour " +
        "day_microsecond day_minute day_second deallocate dec decimal declare default definer delayed delete desc describe " +
        "deterministic distinct distinctrow div do double drop dual duplicate each else elseif enclosed end engine engines " +
        "enum errors escaped event events exists exit explain extended false fetch fields first float for force foreign " +
        "format from full fulltext function general global grant grants group handler having high_priority hour_microsecond " +
        "hour_minute hour_second identified if ignore in index indexes infile inner inout insensitive insert int integer " +
        "interval into invoker is isolation iterate join json key keys kill language last leading leave left level like " +
        "limit linear lines load local localtime localtimestamp lock logs long longblob longtext loop low_priority match " +
        "mediumblob mediumint mediumtext minute_microsecond minute_second mod mode modifies modify natural no not " +
        "no_write_to_binlog null numeric offset on open optimize option optionally or order out outer outfile over " +
        "partition password precision prepare primary privileges procedure processlist purge range read reads real " +
        "recursive references regexp release rename repeat repeatable replace require restrict return returning returns " +
        "revoke right rlike rollback row rows savepoint schema schemas security select sensitive separator sequence " +
        "serializable session set share show signal smallint spatial specific sql sql_big_result sql_calc_found_rows " +
        "sql_no_cache sql_small_result sqlexception sqlstate sqlwarning ssl start starting status straight_join table " +
        "tables temporary terminated text then time timestamp tinyblob tinyint tinytext to trailing transaction trigger " +
        "triggers true truncate uncommitted undo union unique unknown unlock unsigned update usage use user using utc_date " +
        "utc_time utc_timestamp value values varbinary varchar varcharacter variables varying view warnings when where " +
        "while window with work write xor year year_month zerofill";

    private const string Functions =
        "abs acos adddate addtime aes_decrypt aes_encrypt ascii asin atan avg benchmark bin bit_and bit_count bit_length " +
        "bit_or bit_xor cast ceil ceiling char_length character_length coalesce concat concat_ws connection_id conv " +
        "convert_tz cos cot count crc32 cume_dist curdate curtime date_add date_format date_sub datediff dayname dayofmonth " +
        "dayofweek dayofyear degrees dense_rank elt exp export_set extract field find_in_set first_value floor found_rows " +
        "from_base64 from_days from_unixtime get_lock greatest group_concat hex ifnull inet_aton inet_ntoa instr isnull " +
        "json_array json_arrayagg json_contains json_extract json_keys json_object json_objectagg json_query json_set " +
        "json_unquote json_value lag last_day last_insert_id last_value lcase lead least length ln locate log log10 log2 " +
        "lower lpad ltrim makedate maketime max md5 microsecond mid min minute month monthname now nth_value ntile nullif " +
        "oct ord percent_rank period_add period_diff pi position pow power quarter quote radians rand rank regexp_instr " +
        "regexp_replace regexp_substr reverse round row_count row_number rpad rtrim sec_to_time second sha sha1 sha2 sign " +
        "sin sleep soundex space sqrt std stddev str_to_date strcmp subdate substr substring substring_index subtime sum " +
        "sysdate tan time_format time_to_sec timediff timestampadd timestampdiff to_base64 to_days to_seconds trim ucase " +
        "unhex unix_timestamp upper utc_date uuid uuid_short var_pop var_samp variance version week weekday weekofyear yearweek";

    public SqlEditor()
    {
        BorderStyle = ScintillaNET.BorderStyle.None;
        Technology = Technology.DirectWrite;
        LexerName = "sql";
        SetProperty("lexer.sql.backticks.identifier", "1");
        SetProperty("lexer.sql.numbersign.comment", "1");
        SetProperty("sql.backslash.escapes", "1");
        SetProperty("fold", "0");

        StyleResetDefault();
        Styles[Style.Default].Font = "Consolas";
        Styles[Style.Default].SizeF = 10f;
        ApplyTheme(ThemeManager.Current);

        SetKeywords(0, Keywords);
        SetKeywords(1, Functions);

        Margins[0].Type = MarginType.Number;
        Margins[0].Width = TextWidth(Style.LineNumber, "99999") + 8;
        Margins[1].Width = 0;
        TabWidth = 4;
        UseTabs = false;
        MultipleSelection = true;
        WrapIndentMode = WrapIndentMode.Same;
        ApplyWordWrap();

        Indicators[ErrorIndicator].Style = IndicatorStyle.Squiggle;

        // keys the main window uses
        ClearCmdKey(Keys.Control | Keys.T);
        ClearCmdKey(Keys.Control | Keys.Enter);

        // Scintilla.NET turns Scintilla's own drop target off; the WinForms one takes names from the tree (and passes
        // files on to the window, which would otherwise get every drop over the editor and refuse text)
        AllowDrop = true;
    }

    // ---- drop -----------------------------------------------------------------------------------

    private bool IsName(DragEventArgs e) => !ReadOnly && e.Data?.GetDataPresent(NameFormat) == true;

    /// <summary>The position under a screen point.</summary>
    private int PositionAt(int screenX, int screenY)
    {
        var p = PointToClient(new Point(screenX, screenY));
        return CharPositionFromPoint(p.X, p.Y);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (IsName(e))
        {
            _beforeDrag ??= (AnchorPosition, CurrentPosition);
            Focus(); // the caret shows where the name goes
        }
        DragMove(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        DragMove(e);
    }

    private void DragMove(DragEventArgs e)
    {
        if (IsName(e))
        {
            e.Effect = DragDropEffects.Copy;
            SetEmptySelection(PositionAt(e.X, e.Y));
        }
        else e.Effect = FindForm() is IFileDropTarget window ? window.FileDragEffect(e.Data) : DragDropEffects.None;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        if (_beforeDrag is { } before) SetSelection(before.Caret, before.Anchor);
        _beforeDrag = null;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _beforeDrag = null;
        if (IsName(e) && e.Data!.GetData(DataFormats.UnicodeText) is string text)
            InsertName(PositionAt(e.X, e.Y), text);
        else if (e.Data is not null && FindForm() is IFileDropTarget window)
            window.DropFiles(e.Data);
    }

    /// <summary>A dropped name goes in at the position; the caret goes after it and the editor takes the focus.</summary>
    internal void InsertName(int position, string text)
    {
        position = Math.Clamp(position, 0, TextLength);
        InsertText(position, text);
        SetEmptySelection(position + text.Length);
        Focus();
    }

    /// <summary>Syntax colours for the theme. StyleClearAll keeps the font and resets every style to the default colours.</summary>
    public void ApplyTheme(Theme t)
    {
        Styles[Style.Default].BackColor = t.EdBack;
        Styles[Style.Default].ForeColor = t.EdText;
        StyleClearAll();
        foreach (var s in new[] { Style.Sql.Comment, Style.Sql.CommentLine, Style.Sql.CommentDoc, Style.Sql.CommentLineDoc })
            Styles[s].ForeColor = t.EdComment;
        Styles[Style.Sql.Number].ForeColor = t.EdNumber;
        Styles[Style.Sql.Word].ForeColor = t.EdKeyword;
        Styles[Style.Sql.Word].Bold = true;
        Styles[Style.Sql.Word2].ForeColor = t.EdFunction;
        Styles[Style.Sql.String].ForeColor = t.EdString;
        Styles[Style.Sql.Character].ForeColor = t.EdString;
        Styles[Style.Sql.Operator].ForeColor = t.EdOperator;
        Styles[Style.Sql.QuotedIdentifier].ForeColor = t.EdIdent;
        Styles[Style.LineNumber].ForeColor = t.EdLineText;
        Styles[Style.LineNumber].BackColor = t.EdLineBack;
        CaretForeColor = t.EdCaret;
        CaretLineBackColor = t.EdCaretLine;
        SelectionBackColor = t.EdSelection;
        SelectionInactiveBackColor = t.IsDark ? Color.FromArgb(58, 61, 65) : Color.FromArgb(220, 220, 220);
        Indicators[ErrorIndicator].ForeColor = t.Error;
    }

    /// <summary>Word wrap as set in View → Word wrap; with wrap on there is no horizontal scrollbar.</summary>
    public void ApplyWordWrap()
    {
        bool wrap = AppSettings.Current.WordWrap;
        WrapMode = wrap ? ScintillaNET.WrapMode.Word : ScintillaNET.WrapMode.None;
        HScrollBar = !wrap;
    }

    /// <summary>Saves the setting and applies it to every open editor: all connections' tabs and open dialogs.</summary>
    public static void SetWordWrap(bool on)
    {
        AppSettings.Current.WordWrap = on;
        AppSettings.Current.Save();
        foreach (Form f in Application.OpenForms) Apply(f);

        static void Apply(Control c)
        {
            if (c is SqlEditor e) e.ApplyWordWrap();
            foreach (Control child in c.Controls) Apply(child);
        }
    }

    public void MarkError(int start, int end)
    {
        ClearError();
        IndicatorCurrent = ErrorIndicator;
        IndicatorFillRange(start, Math.Max(1, end - start));
        SetSelection(end, start);
        ScrollRange(start, end);
    }

    public void ClearError()
    {
        IndicatorCurrent = ErrorIndicator;
        IndicatorClearRange(0, TextLength);
    }
}
