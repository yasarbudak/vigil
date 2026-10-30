using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vigil.Configuration;
using Vigil.Install;

namespace Vigil.Tray;

/// <summary>What the settings window starts with.</summary>
/// <param name="Problems">What is wrong with the file as it is now (the loader's and the rules' own messages); empty when it is fine.</param>
internal sealed record SettingsLoad(SettingsForm Form, IReadOnlyList<string> Problems);

/// <summary>The outcome of a save: nothing was written when <see cref="Errors"/> is not empty.</summary>
internal sealed record SaveResult(IReadOnlyList<string> Errors)
{
    public bool Saved => Errors.Count == 0;
}

internal interface ISettingsStore
{
    SettingsLoad Load();

    SaveResult Save(SettingsForm form);
}

/// <summary>
/// Reads and writes config.json for the settings window. config.json stays the single configuration source, and the daemon's
/// loader (<see cref="VigilConfiguration"/>) is the only judge of it: <see cref="Save"/> gets the new text accepted by that loader
/// before it writes a byte, so a saved file is one the daemon starts with.
/// </summary>
/// <remarks>
/// The daemon's loader ignores keys it does not know and the window shows only some of the known ones (not PollInterval,
/// VerifyDelay, DegradedRetryInterval, Linux.Backend, and the sections of the other operating systems), so the file is edited as
/// a JSON tree: every key the window does not own is written back as it was, whatever it is. Comments, which the loader accepts,
/// are not kept (the writer has no place for them), and the file is written with two-space indentation.
/// A file that is not valid JSON is never overwritten: the window refuses and says why.
/// </remarks>
/// <param name="platformSection">The section whose <c>Strategy</c> the window edits: <see cref="PlatformSections"/>.</param>
internal sealed class SettingsStore(VigilPaths paths, string platformSection) : ISettingsStore
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public SettingsLoad Load()
    {
        try
        {
            var settings = VigilConfiguration.Load(paths.ConfigPath);
            return new SettingsLoad(SettingsForm.From(settings, platformSection), settings.Validate());
        }
        catch (ConfigurationException ex)
        {
            // The window still opens, with the defaults and the reason; Save refuses to overwrite a file it cannot read.
            return new SettingsLoad(SettingsForm.From(VigilConfiguration.LoadFromJson("{}", paths.ConfigPath), platformSection), [ex.Message]);
        }
    }

    public SaveResult Save(SettingsForm form)
    {
        JsonObject root;
        try
        {
            root = ReadRoot();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail($"{paths.ConfigPath} cannot be edited: {ex.Message} Fix or remove it first; nothing was changed.");
        }

        // First pass: the text as typed, so that the loader parses it and applies its rules. Nothing of it is kept.
        VigilSettings validated;
        try
        {
            validated = VigilConfiguration.LoadFromJsonValidated(Render(Apply(root, form)), paths.ConfigPath);
        }
        catch (ConfigurationException ex)
        {
            return Fail(ex.Message);
        }

        // Second pass: what is written is the loader's own reading in the notation the documentation uses, and that exact text
        // is accepted by the loader once more before it is written.
        var text = Render(Apply(root, SettingsForm.From(validated, platformSection)));
        try
        {
            VigilConfiguration.LoadFromJsonValidated(text, paths.ConfigPath);
        }
        catch (ConfigurationException ex)
        {
            return Fail(ex.Message);
        }

        try
        {
            paths.EnsureDataDirectory();
            InstallFiles.WriteAtomically(paths.ConfigPath, text, InstallFiles.PrivateFileMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Cannot write {paths.ConfigPath}: {ex.Message}");
        }

        return new SaveResult([]);
    }

    private static SaveResult Fail(string error) => new([error]);

    /// <summary>The file as a JSON object; a missing or empty file is an empty object.</summary>
    private JsonObject ReadRoot()
    {
        if (!File.Exists(paths.ConfigPath))
        {
            return [];
        }

        var text = File.ReadAllText(paths.ConfigPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonNode.Parse(text, nodeOptions: null, DocumentOptions) as JsonObject
            ?? throw new InvalidDataException("The top level of the file is not a JSON object.");
    }

    private static string Render(JsonObject root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            root.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>A copy of <paramref name="root"/> with the keys the window owns set from <paramref name="form"/>.</summary>
    private JsonObject Apply(JsonObject root, SettingsForm form)
    {
        var copy = (JsonObject)root.DeepClone();

        var vigil = Section(copy, "Vigil");
        vigil[KeyOf(vigil, "Threshold")] = form.Threshold;
        vigil[KeyOf(vigil, "PauseOnLock")] = form.PauseOnLock;
        vigil[KeyOf(vigil, "PauseOnBattery")] = form.PauseOnBattery;

        var schedule = Section(vigil, "Schedule");
        var days = KeyOf(schedule, "ActiveDays");
        if (form.ActiveDays.Count == SettingsForm.AllDays.Count)
        {
            // Every day is written as no key: the documented way, and the only way that is valid for "all".
            schedule.Remove(days);
        }
        else
        {
            schedule[days] = new JsonArray([.. form.ActiveDays.Select(day => (JsonNode?)JsonValue.Create(day.ToString()))]);
        }

        schedule[KeyOf(schedule, "Start")] = form.ScheduleStart;
        schedule[KeyOf(schedule, "End")] = form.ScheduleEnd;

        var activityLog = Section(vigil, "ActivityLog");
        activityLog[KeyOf(activityLog, "Enabled")] = form.ActivityLogEnabled;

        // A number as the documentation writes it; text that is not one is passed on as it is, for the loader to refuse.
        activityLog[KeyOf(activityLog, "RetentionDays")] = int.TryParse(form.RetentionDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention)
            ? retention
            : form.RetentionDays;

        var platform = Section(copy, platformSection);
        platform[KeyOf(platform, "Strategy")] = form.Strategy;

        return copy;
    }

    /// <summary>
    /// The existing object under <paramref name="name"/>, or a new one. Keys are matched without regard to case, as the
    /// configuration system does, so "vigil" in a hand-written file is reused and not duplicated (a duplicate would make the
    /// daemon refuse the file). A value that is not an object is replaced: it could not have been read as a section anyway.
    /// </summary>
    private static JsonObject Section(JsonObject parent, string name)
    {
        var key = KeyOf(parent, name);
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static string KeyOf(JsonObject parent, string name) =>
        parent.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ?? name;
}
