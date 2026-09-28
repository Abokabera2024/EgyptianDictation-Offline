using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.WordAddIn;

[ComVisible(true)]
[Guid(ClassId)]
[ProgId(ProgId)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IEgyptianDictationBridge))]
public sealed class WordAddIn : IDTExtensibility2, IRibbonExtensibility, IEgyptianDictationBridge
{
#if PREVIEW
    public const string ClassId = "63B19D6A-2D3F-4D7C-AB71-F2C85D82A497";
    public const string ProgId = "EgyptianDictation.WordAddIn.Preview";
#else
    public const string ClassId = "A90C9D0B-14F7-43BF-BEF9-6B2C85D1F50E";
    public const string ProgId = "EgyptianDictation.WordAddIn";
#endif

    private readonly WordPipeClient _client = new();
    private readonly Timer _pollTimer = new() { Interval = 350 };
    private dynamic? _application;
    private string? _sessionId;
    private Task<MessageEnvelope>? _pending;
    private string? _pendingType;
    private bool _stopping;
    private bool _stopSent;
    private int _failures;
    private readonly HashSet<string> _insertedCommits = new(StringComparer.Ordinal);

    public WordAddIn()
    {
        Log("constructor");
        _pollTimer.Tick += PollTimerOnTick;
    }

    public string GetCustomUI(string ribbonId) =>
        "<customUI xmlns='http://schemas.microsoft.com/office/2009/07/customui'>" +
        "<ribbon><tabs><tab id='EgyptianDictationTab' label='" +
#if PREVIEW
        "الإملاء التجريبي الجديد" +
#else
        "الإملاء دون إنترنت" +
#endif
        "'>" +
        "<group id='DictationGroup' label='إملاء عربي ومصري'>" +
        "<button id='StartDictation' label='بدء الإملاء' size='large' imageMso='Microphone' onAction='OnStartDictation'/>" +
        "<button id='StopDictation' label='إيقاف' size='large' imageMso='Stop' onAction='OnStopDictation'/>" +
        "</group><group id='AboutGroup' label='معلومات'>" +
        "<button id='AboutDictation' label='عن البرنامج' onAction='OnAbout'/>" +
        "</group></tab></tabs></ribbon></customUI>";

    public void OnAbout(object control) =>
        MessageBox.Show(ProductAttribution.FullNotice, "الإملاء العربي والمصري دون إنترنت",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void OnStartDictation(object control)
    {
        Log("OnStartDictation");
        try
        {
            StartFromWord(_application ?? throw new InvalidOperationException("Word is not connected."));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "الإملاء دون إنترنت", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void OnStopDictation(object control)
    {
        Log("OnStopDictation");
        try
        {
            StopFromWord();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "الإملاء دون إنترنت", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void StartFromWord(object application)
    {
        if (_sessionId is not null)
            return;
        _application = application;
        dynamic selection = _application.Selection;
        if ((int)selection.Start != (int)selection.End)
            selection.Collapse(0); // wdCollapseEnd
        _sessionId = Guid.NewGuid().ToString("N");
        _stopping = false;
        _stopSent = false;
        _failures = 0;
        BeginRequest(MessageTypes.StartSession);
        SetWordStatus("الإملاء: تجهيز المحرك...");
        _pollTimer.Start();
        Log("StartFromWord");
    }

    public void StopFromWord()
    {
        if (_sessionId is null) return;
        _stopping = true;
        SetWordStatus("الإملاء: إكمال آخر كلام...");
        _pollTimer.Start();
        Log("StopFromWord");
    }

    public void OnConnection(object application, ExtConnectMode connectMode, object addInInst, ref Array custom)
    {
        Log($"OnConnection:{connectMode}");
        _application = application;
    }

    public void OnDisconnection(ExtDisconnectMode removeMode, ref Array custom)
    {
        _pollTimer.Stop();
        _client.Dispose();
        _application = null;
    }

    public void OnAddInsUpdate(ref Array custom) { }
    public void OnStartupComplete(ref Array custom) { }
    public void OnBeginShutdown(ref Array custom) => _pollTimer.Stop();

    private void PollTimerOnTick(object? sender, EventArgs e)
    {
        if (_sessionId is null || _application is null)
            return;
        try
        {
            if (_pending is { IsCompleted: false })
                return;
            if (_pending is not null)
            {
                var response = _pending.GetAwaiter().GetResult();
                var completedType = _pendingType;
                _pending = null;
                _pendingType = null;
                if (completedType == MessageTypes.StartSession && response.Type == MessageTypes.Error)
                {
                    _pollTimer.Stop();
                    _sessionId = null;
                    SetWordStatus("الإملاء: تعذر البدء");
                    MessageBox.Show(WordPipeClient.DeserializeErrorMessage(response), "الإملاء دون إنترنت",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ThrowIfError(response);
                _failures = 0;
                if (response.Type == MessageTypes.CommitText)
                {
                    var committed = WordPipeClient.DeserializePayload<TextEvent>(response);
                    if ((committed.NewParagraphBefore || !string.IsNullOrWhiteSpace(committed.Text)) &&
                        _insertedCommits.Add(committed.CommitId))
                    {
                        try
                        {
                            // Read the live Word caret for every phrase. A saved Range keeps
                            // inserting at the session's original position after a user clicks elsewhere.
                            dynamic selection = _application.Selection;
                            if ((int)selection.Start != (int)selection.End)
                                selection.Collapse(0); // wdCollapseEnd: never replace selected text.
                            selection.InsertAfter(DictationInsertion.Compose(committed));
                        }
                        catch
                        {
                            _insertedCommits.Remove(committed.CommitId);
                            throw;
                        }
                        // InsertAfter expands Selection; leave the caret after the new text.
                        // A collapse failure must not make the same commit insert twice.
                        try { _application.Selection.Collapse(0); }
                        catch (COMException ex) { Log($"caret collapse error: {ex}"); }
                    }
                    BeginRequest(MessageTypes.CommitAck,
                        SerializeAck(committed.CommitId));
                    return;
                }
                if (completedType == MessageTypes.GetStatus && _stopping &&
                    response.Type == MessageTypes.EngineState)
                {
                    var state = WordPipeClient.DeserializePayload<EngineStateEvent>(response);
                    if (state.State == "ready" && _stopSent)
                    {
                        _pollTimer.Stop();
                        _sessionId = null;
                        SetWordStatus("الإملاء: جاهز");
                        return;
                    }
                }
                if (completedType == MessageTypes.GetStatus && response.Type == MessageTypes.EngineState)
                {
                    var state = WordPipeClient.DeserializePayload<EngineStateEvent>(response);
                    if (state.State == "error")
                    {
                        _pollTimer.Stop();
                        _sessionId = null;
                        MessageBox.Show(state.Detail ?? "تعذر بدء الإملاء.", "الإملاء دون إنترنت",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    SetWordStatus(state.State == "preparing" ? "الإملاء: تجهيز المحرك..." :
                        state.State == "draining" ? "الإملاء: إكمال آخر كلام..." :
                        state.State == "listening" ? "الإملاء: يستمع الآن" : "الإملاء: جاهز");
                }
            }
            if (_stopping && !_stopSent)
            {
                _stopSent = true;
                BeginRequest(MessageTypes.StopSession);
            }
            else BeginRequest(MessageTypes.GetStatus);
        }
        catch (Exception ex)
        {
            _pending = null;
            _pendingType = null;
            Log($"poll error: {ex}");
            if (++_failures == 5)
                MessageBox.Show("الاتصال بالإملاء متعثر، وسأواصل محاولة الاستعادة تلقائيًا. " + ex.Message,
                    "الإملاء دون إنترنت", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BeginRequest(string type, string? payload = null)
    {
        var session = _sessionId ?? throw new InvalidOperationException("No active session.");
        _pendingType = type;
        _pending = Task.Run(() => _client.Send(type, session, payload));
    }

    private static string SerializeAck(string commitId) =>
        "{\"commitId\":\"" + commitId + "\"}";

    private void SetWordStatus(string status)
    {
        try { if (_application is not null) _application.StatusBar = status; }
        catch (COMException) { /* Word is temporarily busy. */ }
    }

    private static void ThrowIfError(MessageEnvelope response)
    {
        if (response.Type == MessageTypes.Error)
            throw new InvalidOperationException(WordPipeClient.DeserializeErrorMessage(response));
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "EgyptianDictation-wordaddin.log"),
                $"{DateTime.UtcNow:o} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
