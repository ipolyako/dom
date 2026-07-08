namespace FastDOM.Infrastructure.Config;

public class TokenSourceConfig
{
    public string Host                { get; set; } = "";
    public int    Port                { get; set; } = 1527;
    public string Database            { get; set; } = "";
    public string Schema              { get; set; } = "ROCH";
    public string User                { get; set; } = "";
    public string Password            { get; set; } = "";
    public string AuthRefTable        { get; set; } = "AUTHREF";
    public string TokenTable          { get; set; } = "TOKEN";
    public string AppKeyColumn        { get; set; } = "APPKEY";
    public string AppSecretColumn     { get; set; } = "APPSECRET";
    public string AccountHashColumn   { get; set; } = "ACCOUNTHASH";
    public string RefreshTokenColumn  { get; set; } = "REFRESHTOKEN";
    public string Purpose            { get; set; } = "TRADE";
    public string AccountId          { get; set; } = "";
}
