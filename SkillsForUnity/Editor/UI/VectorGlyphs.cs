using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    internal enum VectorGlyph
    {
        Refresh,
        Check,
    }

    /// <summary>
    /// Small glyphs painted with Painter2D in the element's resolved text color, so disclosure triangles and toolbar
    /// icons share one size and stroke weight instead of mixing font characters with editor bitmaps.
    /// </summary>
    internal static class VectorGlyphs
    {
        private const string OpenClass = "disclosure--open";
        private const float StrokeWidth = 1.5f;

        /// <summary>A filled triangle pointing right when closed and down when open; sized and colored by `.disclosure`.</summary>
        internal static VisualElement Disclosure(bool open, string name = null)
        {
            var element = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            element.AddToClassList("disclosure");
            element.EnableInClassList(OpenClass, open);
            element.generateVisualContent += DrawDisclosure;
            return element;
        }

        internal static void SetOpen(VisualElement disclosure, bool open)
        {
            if (disclosure == null) return;
            disclosure.EnableInClassList(OpenClass, open);
            disclosure.MarkDirtyRepaint();
        }

        /// <summary>Replaces the button's text with <paramref name="glyph"/>, painted in a `.toolbar-glyph` child.</summary>
        internal static void SetIcon(Button button, VectorGlyph glyph)
        {
            if (button == null) return;
            button.text = string.Empty;
            button.AddToClassList("toolbar-glyph-btn");
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("toolbar-glyph");
            icon.generateVisualContent += glyph == VectorGlyph.Refresh ? (System.Action<MeshGenerationContext>)DrawRefresh : DrawCheck;
            button.Add(icon);
        }

        private static void DrawDisclosure(MeshGenerationContext context)
        {
            var rect = context.visualElement.contentRect;
            if (rect.width <= 0f || rect.height <= 0f) return;
            var c = rect.center;
            var painter = context.painter2D;
            painter.fillColor = context.visualElement.resolvedStyle.color;
            painter.BeginPath();
            if (context.visualElement.ClassListContains(OpenClass))
            {
                painter.MoveTo(new Vector2(c.x - 4f, c.y - 2.5f));
                painter.LineTo(new Vector2(c.x + 4f, c.y - 2.5f));
                painter.LineTo(new Vector2(c.x, c.y + 2.5f));
            }
            else
            {
                painter.MoveTo(new Vector2(c.x - 2.5f, c.y - 4f));
                painter.LineTo(new Vector2(c.x + 2.5f, c.y));
                painter.LineTo(new Vector2(c.x - 2.5f, c.y + 4f));
            }
            painter.ClosePath();
            painter.Fill();
        }

        private static void DrawRefresh(MeshGenerationContext context)
        {
            var rect = context.visualElement.contentRect;
            if (rect.width <= 0f || rect.height <= 0f) return;
            var color = context.visualElement.resolvedStyle.color;
            var c = rect.center;
            float r = Mathf.Min(rect.width, rect.height) * 0.5f - 2f;
            const float start = -40f, end = 250f;
            var painter = context.painter2D;
            painter.strokeColor = color;
            painter.lineWidth = StrokeWidth;
            painter.lineCap = LineCap.Butt;
            painter.BeginPath();
            painter.Arc(c, r, Angle.Degrees(start), Angle.Degrees(end), ArcDirection.Clockwise);
            painter.Stroke();

            // Arrowhead at the end of the arc, pointing along the clockwise tangent.
            float a = end * Mathf.Deg2Rad;
            var radial = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var tangent = new Vector2(-radial.y, radial.x);
            var tip = c + radial * r;
            painter.fillColor = color;
            painter.BeginPath();
            painter.MoveTo(tip + tangent * 2.6f);
            painter.LineTo(tip + radial * 2.6f);
            painter.LineTo(tip - radial * 2.6f);
            painter.ClosePath();
            painter.Fill();
        }

        private static void DrawCheck(MeshGenerationContext context)
        {
            var rect = context.visualElement.contentRect;
            if (rect.width <= 0f || rect.height <= 0f) return;
            var painter = context.painter2D;
            painter.strokeColor = context.visualElement.resolvedStyle.color;
            painter.lineWidth = StrokeWidth;
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin + rect.width * 0.18f, rect.yMin + rect.height * 0.54f));
            painter.LineTo(new Vector2(rect.xMin + rect.width * 0.42f, rect.yMin + rect.height * 0.76f));
            painter.LineTo(new Vector2(rect.xMin + rect.width * 0.84f, rect.yMin + rect.height * 0.28f));
            painter.Stroke();
        }
    }
}

// Producer:Betsy
