namespace RemoteManager.Core.Model;

/// <summary>
/// A folder for connections. Groups nest through <see cref="ParentId"/> and carry credentials that
/// connections without their own inherit (see <see cref="Credentials.CredentialService.Resolve"/>).
/// </summary>
public sealed class ConnectionGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public string Description { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsCollapsed { get; set; }
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public CredentialRef? Credential { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
