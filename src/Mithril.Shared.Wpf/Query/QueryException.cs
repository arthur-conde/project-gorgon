using System;

namespace Mithril.Shared.Wpf.Query;

public sealed class QueryException : Exception
{
    public int Position { get; }

    public QueryException(string message, int position) : base(message)
    {
        Position = position;
    }
}
