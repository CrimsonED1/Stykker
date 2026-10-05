namespace Stykker.NanoCut.Demo.Pages;

/// <summary>One hand-written page: what it is for, in one line.</summary>
/// <param name="Group">Sidebar group it sits in.</param>
/// <param name="Title">What the link says.</param>
/// <param name="Route">Relative route, as the sidebar links it.</param>
/// <param name="Blurb">What the page is for, shown on the overview.</param>
public sealed record DemoPage(string Group, string Title, string Route, string Blurb);

/// <summary>
/// The pages that are not <see cref="Scenes.DemoScene"/>s. The scene pages come from <see cref="Scenes.SceneCatalog"/>
/// and cannot go missing from here; these are one list, read by the sidebar and by the overview, so the two cannot
/// disagree about what exists.
/// </summary>
public static class PageIndex
{
    /// <summary>In sidebar order. The group a page is in is the order it first appears.</summary>
    public static IReadOnlyList<DemoPage> All { get; } =
    [
        new("Machines", "Lathe (X = diameter, Z)", "machine/lathe",
            "The tool follows a contour in the r-z half plane, cut exactly in that half plane and revolved. Volume checked against Pappus."),

        new("Machines", "3-axis mill (X, Y, Z)", "machine/mill",
            "A ball-nose or flat mill on a zig-zag path; every move is an exact Minkowski sum of the tool with the move."),

        new("Machines", "Spinning disc (saw / wheel)", "spinning",
            "Saw blades and grinding wheels as solids of revolution, cut from the profile outward."),

        new("Machines", "Grinding grains", "grinding",
            "One abrasive grain after another on its own trochoid. Each grain only removes what the grains before it left, "
            + "so the surface roughness is a result and not an input."),

        new("Machines", "Free-form: cube shapes cube", "cubes",
            "Point-wise deformation instead of a tool: no cutting, no stock, just a map."),

        new("Preview", "Preview: exact vs CPU vs GPU", "preview",
            "One program cut three ways: exactly, as a height field on the CPU, and as a height field on the GPU. "
            + "The panel times all three and prints each preview's deviation from the exact result."),

        new("Measurement", "Profiles (sections of 3D parts)", "profiles",
            "Plane sections, axial and radial profiles, line profiles with Ra and Rz, deviation from a nominal form, "
            + "export as CSV and SVG."),

        new("Measurement", "Inspection (colour = deviation in nm)", "inspect",
            "The measuring microscope. Every vertex is coloured by its measured deviation from the nominal form, drawn "
            + "unlit so no highlight can falsify a number. Scale bar, 1 nm lattice, and a click that reports the exact "
            + "integer nanometre from the server rather than the float32 the browser drew from."),

        new("Verification", "Self test", "selftest",
            "Recomputes the library's own checks in the browser, so a build can be trusted on the machine it runs on."),
    ];
}
