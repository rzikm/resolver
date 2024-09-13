namespace Resolver;

internal enum QueryType
{
    Address = 1,
    NameServer = 2,
    MailExchange = 15,
    Text = 16,
    IP6Address = 28,
    Service = 33,
    All = 255
}