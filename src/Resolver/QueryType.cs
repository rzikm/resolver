namespace Resolver;

internal enum QueryType
{
    Address = 1,
    NameServer = 2,
    Alias = 5, // CNAME
    MailExchange = 15,
    Text = 16,
    IP6Address = 28,
    Service = 33,
    All = 255
}