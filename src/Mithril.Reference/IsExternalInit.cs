// Polyfill for netstandard2.0: the C# compiler emits a reference to this type
// when compiling records (positional or otherwise). .NET 5+ provides it in
// System.Runtime; earlier targets need the type declared manually.
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
