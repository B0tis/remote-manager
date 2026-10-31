using Avalonia.Controls;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections;

/// <summary>
/// The type-specific part of the connection editor (<see cref="IConnectionHandler.CreateSettingsEditor"/>).
/// The editor shows <see cref="View"/> below the common fields and swaps it when the type changes.
/// </summary>
public interface ISettingsEditor
{
    Control View { get; }

    /// <summary>Fills the view; <paramref name="settings"/> is the record of the handler's type.</summary>
    void Load(ConnectionSettings settings);

    /// <summary>German error message for the editor, or null when the input is valid.</summary>
    string? Validate();

    /// <summary>The edited settings (a new record; the loaded one is left alone).</summary>
    ConnectionSettings Commit();
}
