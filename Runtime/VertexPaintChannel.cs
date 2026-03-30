using UnityEngine;

namespace JuiceAI.VertexPainter
{
    public enum VertexPaintChannel
    {
        Red = 0,
        Green = 1,
        Blue = 2,
        Alpha = 3
    }

    public static class VertexPaintChannelUtility
    {
        public static Color32 DefaultColor => new(0, 0, 0, byte.MaxValue);

        public static byte GetByte(Color32 color, VertexPaintChannel channel)
        {
            return channel switch
            {
                VertexPaintChannel.Red => color.r,
                VertexPaintChannel.Green => color.g,
                VertexPaintChannel.Blue => color.b,
                VertexPaintChannel.Alpha => color.a,
                _ => color.r
            };
        }

        public static float GetValue01(Color32 color, VertexPaintChannel channel)
        {
            return GetByte(color, channel) / 255f;
        }

        public static Color32 WithByte(Color32 color, VertexPaintChannel channel, byte value)
        {
            switch (channel)
            {
                case VertexPaintChannel.Red:
                    color.r = value;
                    break;
                case VertexPaintChannel.Green:
                    color.g = value;
                    break;
                case VertexPaintChannel.Blue:
                    color.b = value;
                    break;
                case VertexPaintChannel.Alpha:
                    color.a = value;
                    break;
            }

            return color;
        }

        public static Color32 MoveToward(Color32 color, VertexPaintChannel channel, byte target, float weight)
        {
            byte current = GetByte(color, channel);
            byte next = (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(current, target, Mathf.Clamp01(weight))),
                0,
                255);

            return WithByte(color, channel, next);
        }
    }
}
