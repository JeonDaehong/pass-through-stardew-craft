using StardewValley;

namespace StardewCraft
{
    /// <summary>The Stardew location the guest is currently simulating, and its walkability grid.</summary>
    internal sealed class SimLocation
    {
        public GameLocation Location { get; }
        public int MapId { get; }
        public int Width { get; }
        public int Height { get; }
        public byte[] Grid { get; }

        public SimLocation(GameLocation location, int mapId, int width, int height, byte[] grid)
        {
            this.Location = location;
            this.MapId = mapId;
            this.Width = width;
            this.Height = height;
            this.Grid = grid;
        }

        public string Key => this.Location.NameOrUniqueName;

        public bool IsWalkable(int x, int y)
        {
            return x >= 0 && y >= 0 && x < this.Width && y < this.Height && this.Grid[y * this.Width + x] == 1;
        }

        public bool IsWalkable(int x, int y, int size)
        {
            for (int dy = 0; dy < size; dy++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    if (!this.IsWalkable(x + dx, y + dy))
                        return false;
                }
            }
            return true;
        }
    }
}
