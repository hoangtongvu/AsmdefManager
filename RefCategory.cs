namespace AsmdefManager;

// Ref categories for references sorting, the underlying values represent sorting order.
internal enum RefCategory
{
    Unknown = 0,
    Registry = 1,
    BuiltIn = 2,
    Embedded = 3,
    Local = 4,
    Git = 5,
    LocalTarball = 6,
    UserAssembly = 7,
}