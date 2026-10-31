using RemoteManager.App.Resources;
using RemoteManager.Core.Extensibility;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections;

/// <summary>
/// Describes a type's start parameters in a few short lines for the detail panel ("NLA an",
/// "Laufwerke umgeleitet"). Discovered like the handlers: add a class next to the handler, no list to edit.
/// Types without one show "Standardeinstellungen" or "Eigene Einstellungen".
/// </summary>
public interface IStartParameterSummary
{
    ConnectionType Type { get; }

    IReadOnlyList<string> Describe(ConnectionSettings settings);
}

/// <summary>The discovered <see cref="IStartParameterSummary"/> implementations.</summary>
public static class StartParameters
{
    private static readonly Lazy<Dictionary<ConnectionType, IStartParameterSummary>> Summaries = new(() =>
        PluginCatalog.Create<IStartParameterSummary>(typeof(StartParameters).Assembly).ToDictionary(s => s.Type));

    public static IReadOnlyList<string> Describe(Connection connection)
    {
        if (Summaries.Value.TryGetValue(connection.Type, out IStartParameterSummary? summary) && summary.Describe(connection.Settings) is { Count: > 0 } lines)
            return lines;
        return [connection.Settings == ConnectionSettings.CreateDefault(connection.Type) ? Strings.StartParams_Default : Strings.StartParams_Custom];
    }
}
