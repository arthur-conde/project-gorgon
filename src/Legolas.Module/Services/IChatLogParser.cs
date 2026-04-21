using Legolas.Domain;

namespace Legolas.Services;

public interface IChatLogParser
{
    GameEvent? TryParse(string line, DateTime timestamp);
}
