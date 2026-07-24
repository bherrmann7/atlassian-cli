namespace AtlCli;

public class AtlassianConfig
{
    public string Email { get; set; } = "";
    public string JiraToken { get; set; } = "";
    public string BitbucketToken { get; set; } = "";
    public string JiraBaseUrl { get; set; } = "";
    public string BitbucketWorkspace { get; set; } = "";
    public string BitbucketRepo { get; set; } = "";

    // Optional. The story-point custom field id (e.g. "customfield_10026"). Leave unset to let
    // the client discover it by name from /rest/api/3/field; set it to pin a specific field when
    // an instance exposes more than one candidate.
    public string StoryPointsField { get; set; } = "";
}
