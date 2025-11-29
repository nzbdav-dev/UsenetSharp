namespace UsenetSharp.Models;

public record UsenetResponse
{
    public required int ResponseCode;
    public required string ResponseMessage;
    
    public UsenetResponseType ResponseType => Enum.IsDefined(typeof(UsenetResponseType), ResponseCode)
        ? (UsenetResponseType)ResponseCode
        : UsenetResponseType.Unknown;
}