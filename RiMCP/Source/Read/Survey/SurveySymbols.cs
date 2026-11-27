using System.Collections.Generic;

namespace RiMCP.Read
{
    internal static class SurveySymbols
    {
        public const char Colonist = 'C';
        public const char Hostile = 'H';
        public const char Structure = '#';
        public const char Water = '~';
        public const char Rock = '^';
        private const int TerrainShare = 4;

        public static readonly Dictionary<char, string> Classes = new Dictionary<char, string>
        {
            { Colonist, "colonist" },
            { Hostile, "hostile" },
            { Structure, "structure" },
            { MapLayerProjection.Unexplored, "unexplored" },
            { Water, "water" },
            { Rock, "rock" },
            { ':', "richSoil" },
            { 't', "trees" },
            { '=', "stockpile" },
            { '%', "growingZone" },
            { '.', "soil" },
            { ',', "poorSoil" },
            { '_', "infertile" }
        };

        public static char Terrain(char symbol)
        {
            switch (symbol)
            {
                case '+':
                case 'b':
                case 'x':
                    return Structure;
                case 'w':
                    return Water;
                default:
                    return symbol;
            }
        }

        public static char Priority(IReadOnlyDictionary<char, int> counts, int cells, char majority)
        {
            if (counts.ContainsKey(Colonist))
            {
                return Colonist;
            }
            if (counts.ContainsKey(Hostile))
            {
                return Hostile;
            }
            if (counts.ContainsKey(Structure))
            {
                return Structure;
            }
            if (EnoughCoverage(counts, Water, cells))
            {
                return Water;
            }
            return EnoughCoverage(counts, Rock, cells) ? Rock : majority;
        }

        private static bool EnoughCoverage(IReadOnlyDictionary<char, int> counts, char symbol, int cells)
        {
            return counts.TryGetValue(symbol, out int seen) && seen * TerrainShare >= cells;
        }

        public static bool IsColonist(char symbol)
        {
            return symbol == '@' || symbol >= 'A' && symbol <= 'Z';
        }
    }
}
