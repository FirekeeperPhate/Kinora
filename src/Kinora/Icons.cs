using Avalonia.Media;

namespace Kinora;

/// <summary>Line icons on a 24×24 grid, drawn with <c>Path.icon</c> (stroke, no fill).</summary>
public static class Icons
{
    public static Geometry Tv { get; } = Geometry.Parse("M3.5,6.5 H20.5 V17.5 H3.5 Z M8.5,21 H15.5 M9,3 L12,6.5 L15,3");
    public static Geometry Dice { get; } = Geometry.Parse("M5.5,3.5 H18.5 A2,2 0 0 1 20.5,5.5 V18.5 A2,2 0 0 1 18.5,20.5 H5.5 A2,2 0 0 1 3.5,18.5 V5.5 A2,2 0 0 1 5.5,3.5 Z M8.5,8.5 V8.6 M15.5,8.5 V8.6 M12,12 V12.1 M8.5,15.5 V15.6 M15.5,15.5 V15.6");
    public static Geometry More { get; } = Geometry.Parse("M6,12 V12.1 M12,12 V12.1 M18,12 V12.1");
    public static Geometry Film { get; } = Geometry.Parse("M4,5 H20 V19 H4 Z M8,5 V19 M16,5 V19 M4,9.5 H8 M4,14.5 H8 M16,9.5 H20 M16,14.5 H20");
    public static Geometry Eye { get; } = Geometry.Parse("M2.5,12 C4.5,8 8,6 12,6 C16,6 19.5,8 21.5,12 C19.5,16 16,18 12,18 C8,18 4.5,16 2.5,12 Z M12,9.5 A2.5,2.5 0 1 0 12.01,9.5 Z");
    public static Geometry Clock { get; } = Geometry.Parse("M12,3 A9,9 0 1 0 12.01,3 Z M12,7.5 V12 L15,14");
    public static Geometry Question { get; } = Geometry.Parse("M12,3 A9,9 0 1 0 12.01,3 Z M9.6,9.4 A2.5,2.5 0 1 1 13.2,11.7 C12.5,12.1 12,12.6 12,13.4 V14 M12,17 V17.2");
    public static Geometry Folder { get; } = Geometry.Parse("M3,7 A1.5,1.5 0 0 1 4.5,5.5 H9 L11,7.5 H19.5 A1.5,1.5 0 0 1 21,9 V17.5 A1.5,1.5 0 0 1 19.5,19 H4.5 A1.5,1.5 0 0 1 3,17.5 Z");
    public static Geometry Tag { get; } = Geometry.Parse("M3.5,11.5 V4.5 A1,1 0 0 1 4.5,3.5 H11.5 L20.5,12.5 L12.5,20.5 Z M8,7 A1,1 0 1 0 8.01,7 Z");
    public static Geometry Sliders { get; } = Geometry.Parse("M4,6 H20 M4,12 H20 M4,18 H20 M9,4 V8 M15,10 V14 M7,16 V20");
    public static Geometry Refresh { get; } = Geometry.Parse("M20,12 A8,8 0 1 1 17.7,6.3 M20,3.5 V8.5 H15");
    public static Geometry Back { get; } = Geometry.Parse("M14.5,5 L7.5,12 L14.5,19");
    public static Geometry Check { get; } = Geometry.Parse("M5,12.5 L10,17 L19,7");
    public static Geometry Search { get; } = Geometry.Parse("M10.5,4 A6.5,6.5 0 1 0 10.51,4 Z M15.5,15.5 L20,20");
    public static Geometry Sort { get; } = Geometry.Parse("M8,4 V20 M4.5,16.5 L8,20 L11.5,16.5 M16,20 V4 M12.5,7.5 L16,4 L19.5,7.5");
    public static Geometry Offline { get; } = Geometry.Parse("M3,3 L21,21 M8.5,8.6 A5,5 0 0 0 7,12.5 M5,10 A9,9 0 0 0 4,13 M12,18.5 V18.6 M15.7,10.5 A5,5 0 0 1 17,12.5 M10,5.2 A9,9 0 0 1 20,10");
}
