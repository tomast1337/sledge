﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Sledge.Common.Mediator;
using Sledge.DataStructures.Geometric;
using Sledge.DataStructures.MapObjects;
using Sledge.DataStructures.Transformations;
using Sledge.Editor.Clipboard;
using Sledge.Editor.History;
using Sledge.Editor.Properties;
using Sledge.Editor.Rendering;
using Sledge.Editor.Tools.TransformationTools;
using Sledge.Settings;
using Sledge.UI;

namespace Sledge.Editor.Tools
{
    /// <summary>
    /// The select tool is used to select objects in several different ways:
    /// 1. Single click in the 2D view will perform edge-detection selection
    /// 2. Single click in the 3D view allows ray-casting selection (with mouse wheel cycling)
    /// 3. Drawing a box in the 2D view and confirming it will select everything in the box
    /// </summary>
    class SelectTool : BaseBoxTool
    {
        private MapObject ChosenItemFor3DSelection { get; set; }
        private List<MapObject> IntersectingObjectsFor3DSelection { get; set; }

        private readonly List<TransformationTool> _tools;
        private TransformationTool _lastTool;
        private TransformationTool _currentTool;

        private bool Transforming { get; set; }
        private Matrix4d? CurrentTransform { get; set; }

        public SelectTool()
        {
            Usage = ToolUsage.Both;
            _tools = new List<TransformationTool>
                         {
                             new ResizeTool(),
                             new RotateTool(),
                             new SkewTool()
                         };
        }

        public override Image GetIcon()
        {
            return Resources.Tool_Select;
        }

        public override string GetName()
        {
            return "Select Tool";
        }

        protected override Color BoxColour
        {
            get { return Color.Yellow; }
        }

        protected override Color FillColour
        {
            get { return Color.FromArgb(128, Color.Gray); }
        }

        public override void ToolSelected()
        {
            Mediator.Subscribe(EditorMediator.SelectionChanged, this);
            SelectionChanged();
            Document.UpdateSelectLists();
        }

        public override void ToolDeselected()
        {
            SetCurrentTool(null);
        }

        #region Current tool

        private void SetCurrentTool(TransformationTool tool)
        {
            if (tool != null) _lastTool = tool;
            _currentTool = tool;
        }

        private void SelectionChanged()
        {
            if (Document == null) return;
            UpdateBoxBasedOnSelection();
            if (State.Action != BoxAction.ReadyToResize && _currentTool != null) SetCurrentTool(null);
            else if (State.Action == BoxAction.ReadyToResize && _currentTool == null) SetCurrentTool(_lastTool ?? _tools[0]);
        }

        /// <summary>
        /// Updates the box based on the currently selected objects.
        /// </summary>
        private void UpdateBoxBasedOnSelection()
        {
            if (Document.Selection.IsEmpty())
            {
                State.BoxStart = State.BoxEnd = null;
                State.Action = BoxAction.ReadyToDraw;
            }
            else
            {
                State.Action = BoxAction.ReadyToResize;
                decimal x1 = Decimal.MaxValue, y1 = Decimal.MaxValue, z1 = Decimal.MaxValue;
                decimal x2 = Decimal.MinValue, y2 = Decimal.MinValue, z2 = Decimal.MinValue;
                foreach (var c in Document.Selection.GetSelectedObjects())
                {
                    var min = c.BoundingBox.Start;
                    var max = c.BoundingBox.End;

                    x1 = Math.Min(x1, min.X);
                    y1 = Math.Min(y1, min.Y);
                    z1 = Math.Min(z1, min.Z);

                    x2 = Math.Max(x2, max.X);
                    y2 = Math.Max(y2, max.Y);
                    z2 = Math.Max(z2, max.Z);
                }
                State.BoxStart = new Coordinate(x1, y1, z1);
                State.BoxEnd = new Coordinate(x2, y2, z2);
            }
        }

        #endregion

        #region Perform selection

        /// <summary>
        /// If ignoreGrouping is disabled, this will convert the list of objects into their topmost group or entity.
        /// </summary>
        /// <param name="objects">The object list to normalise</param>
        /// <param name="ignoreGrouping">True if grouping is being ignored</param>
        /// <returns>The normalised list of objects</returns>
        private static IEnumerable<MapObject> NormaliseSelection(IEnumerable<MapObject> objects, bool ignoreGrouping)
        {
            //TODO should selection flatten?
            return ignoreGrouping
                       ? objects
                       : objects.Select(x => x.FindTopmostParent(y => y is Group || y is Entity) ?? x).Distinct().SelectMany(x => x.FindAll());
        }

        /// <summary>
        /// Deselect (first) a list of objects and then select (second) another list.
        /// </summary>
        /// <param name="objectsToDeselect">The objects to deselect</param>
        /// <param name="objectsToSelect">The objects to select</param>
        /// <param name="deselectAll">If true, this will ignore the objectToDeselect parameter and just deselect everything</param>
        /// <param name="ignoreGrouping">If true, object groups will be ignored</param>
        private void SetSelected(IEnumerable<MapObject> objectsToDeselect, IEnumerable<MapObject> objectsToSelect, bool deselectAll, bool ignoreGrouping)
        {
            if (objectsToDeselect == null) objectsToDeselect = new MapObject[0];
            if (objectsToSelect == null) objectsToSelect = new MapObject[0];

            if (deselectAll)
            {
                objectsToDeselect = Document.Selection.GetSelectedObjects();
                _lastTool = null;
            }

            // Normalise selections
            objectsToDeselect = NormaliseSelection(objectsToDeselect.Where(x => x != null), ignoreGrouping);
            objectsToSelect = NormaliseSelection(objectsToSelect.Where(x => x != null), ignoreGrouping);

            // Don't bother deselecting the objects we're about to select
            objectsToDeselect = objectsToDeselect.Where(x => !objectsToSelect.Contains(x));

            // Perform selections
            var deselected = objectsToDeselect.ToList();
            var selected = objectsToSelect.ToList();

            Document.Selection.Deselect(deselected);
            Document.Selection.Select(selected);

            // Log history
            var hd = new HistorySelect("Deselected objects", deselected, true);
            var hs = new HistorySelect("Selected objects", selected, true);
            var ic = new HistoryItemCollection("Selection changed", new[] {hd, hs});
            Document.History.AddHistoryItem(ic);
        }

        #endregion

        #region 3D selection

        /// <summary>
        /// When the mouse is pressed in the 3D view, we want to select the clicked object.
        /// </summary>
        /// <param name="viewport">The viewport that was clicked</param>
        /// <param name="e">The click event</param>
        protected override void MouseDown3D(Viewport3D viewport, MouseEventArgs e)
        {
            // First, get the ray that is cast from the clicked point along the viewport frustrum
            var ray = viewport.CastRayFromScreen(e.X, e.Y);

            // Grab all the elements that intersect with the ray
            var hits = Document.Map.WorldSpawn.GetAllNodesIntersectingWith(ray);

            // Sort the list of intersecting elements by distance from ray origin
            IntersectingObjectsFor3DSelection = hits
                .Select(x => new { Item = x, Intersection = x.GetIntersectionPoint(ray) })
                .Where(x => x.Intersection != null)
                .OrderBy(x => (x.Intersection - ray.Start).VectorMagnitude())
                .Select(x => x.Item)
                .ToList();

            // By default, select the closest object
            ChosenItemFor3DSelection = IntersectingObjectsFor3DSelection.FirstOrDefault();

            // If Ctrl is down and the object is already selected, we should deselect it instead.
            var list = new[] {ChosenItemFor3DSelection};
            var desel = ChosenItemFor3DSelection != null && KeyboardState.Ctrl && ChosenItemFor3DSelection.IsSelected;
            SetSelected(desel ? list : null, desel ? null : list, !KeyboardState.Ctrl, false);

            Document.UpdateSelectLists();
            State.ActiveViewport = null;
        }

        /// <summary>
        /// When the mouse wheel is scrolled while the mouse is down in the 3D view, cycle through the candidate elements.
        /// </summary>
        /// <param name="viewport">The viewport that was scrolled</param>
        /// <param name="e">The scroll event</param>
        public override void MouseWheel(ViewportBase viewport, MouseEventArgs e)
        {
            // If we're not in 3D cycle mode, carry on
            if (!(viewport is Viewport3D)
                || IntersectingObjectsFor3DSelection == null
                || ChosenItemFor3DSelection == null)
            {
                return;
            }

            var desel = new List<MapObject>();
            var sel = new List<MapObject>();

            // Select (or deselect) the current element
            if (ChosenItemFor3DSelection.IsSelected) desel.Add(ChosenItemFor3DSelection);
            else sel.Add(ChosenItemFor3DSelection);

            // Get the index of the current element
            var index = IntersectingObjectsFor3DSelection.IndexOf(ChosenItemFor3DSelection);
            if (index < 0) return;

            // Move the index in the mouse wheel direction, cycling if needed
            var dir = e.Delta / Math.Abs(e.Delta);
            index = (index + dir) % IntersectingObjectsFor3DSelection.Count;
            if (index < 0) index += IntersectingObjectsFor3DSelection.Count;

            ChosenItemFor3DSelection = IntersectingObjectsFor3DSelection[index];

            // Select (or deselect) the new current element
            if (ChosenItemFor3DSelection.IsSelected) desel.Add(ChosenItemFor3DSelection);
            else sel.Add(ChosenItemFor3DSelection);

            SetSelected(desel, sel, false, false);

            Document.UpdateSelectLists();

            State.ActiveViewport = null;
        }

        /// <summary>
        /// The select tool captures the mouse wheel when the mouse is down in the 3D viewport
        /// </summary>
        /// <returns>True if the select tool is capturing wheel events</returns>
        public override bool IsCapturingMouseWheel()
        {
            return IntersectingObjectsFor3DSelection != null
                   && IntersectingObjectsFor3DSelection.Any()
                   && ChosenItemFor3DSelection != null;
        }

        public override HotkeyInterceptResult InterceptHotkey(HotkeysMediator hotkeyMessage)
        {
            return HotkeyInterceptResult.Continue;
        }

        /// <summary>
        /// Once the mouse is released in the 3D view, the 3D select cycle has finished.
        /// </summary>
        /// <param name="viewport">The 3D viewport</param>
        /// <param name="e">The mouse event</param>
        protected override void MouseUp3D(Viewport3D viewport, MouseEventArgs e)
        {
            IntersectingObjectsFor3DSelection = null;
            ChosenItemFor3DSelection = null;
        }

        #endregion

        #region 2D interaction
        
        protected override Cursor CursorForHandle(ResizeHandle handle)
        {
            var def = base.CursorForHandle(handle);
            return _currentTool == null || handle == ResizeHandle.Center
                       ? def
                       : _currentTool.CursorForHandle(handle) ?? def;
        }

        /// <summary>
        /// When the mouse is hovering over the box, do collision tests against the handles and change the cursor if needed.
        /// </summary>
        /// <param name="viewport">The viewport</param>
        /// <param name="e">The mouse event</param>
        protected override void MouseHoverWhenDrawn(Viewport2D viewport, MouseEventArgs e)
        {
            if (_currentTool == null)
            {
                base.MouseHoverWhenDrawn(viewport, e);
                return;
            }

            var padding = 7 / viewport.Zoom;

            viewport.Cursor = Cursors.Default;
            State.Action = BoxAction.Drawn;
            State.ActiveViewport = null;

            var now = viewport.ScreenToWorld(e.X, viewport.Height - e.Y);
            var start = viewport.Flatten(State.BoxStart);
            var end = viewport.Flatten(State.BoxEnd);

            var ccs = new Coordinate(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), 0);
            var cce = new Coordinate(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y), 0);

            // Check center handle
            if (now.X > ccs.X && now.X < cce.X && now.Y > ccs.Y && now.Y < cce.Y)
            {
                State.Handle = ResizeHandle.Center;
                State.ActiveViewport = viewport;
                State.Action = BoxAction.ReadyToResize;
                viewport.Cursor = CursorForHandle(State.Handle);
                return;
            }

            // Check other handles
            foreach (var handle in _currentTool.GetHandles(start, end, viewport.Zoom).Where(x => _currentTool.FilterHandle(x.Item1)))
            {
                var x = handle.Item2;
                var y = handle.Item3;
                if (now.X < x - padding || now.X > x + padding || now.Y < y - padding || now.Y > y + padding) continue;
                State.Handle = handle.Item1;
                State.ActiveViewport = viewport;
                State.Action = BoxAction.ReadyToResize;
                viewport.Cursor = CursorForHandle(State.Handle);
                return;
            }
        }

        /// <summary>
        /// The select tool will deselect all selected objects if ctrl is not held down when drawing a box.
        /// </summary>
        /// <param name="viewport">The viewport to draw in</param>
        /// <param name="e">The mouse event</param>
        protected override void LeftMouseDownToDraw(Viewport2D viewport, MouseEventArgs e)
        {
            // If we've clicked outside a selection box and not holding down control, clear the selection
            if (!Document.Selection.IsEmpty() && !KeyboardState.Ctrl)
            {
                SetSelected(null, null, true, false);
                Document.UpdateSelectLists();
            }

            // We're drawing a selection box, so clear the current tool
            SetCurrentTool(null);

            base.LeftMouseDownToDraw(viewport, e);
        }

        private MapObject SelectionTest(Viewport2D viewport, MouseEventArgs e)
        {
            // Create a box to represent the click, with a tolerance level
            var unused = viewport.GetUnusedCoordinate(new Coordinate(100000, 100000, 100000));
            var tolerance = 4 / viewport.Zoom; // Selection tolerance of four pixels
            var used = viewport.Expand(new Coordinate(tolerance, tolerance, 0));
            var add = used + unused;
            var click = viewport.Expand(viewport.ScreenToWorld(e.X, viewport.Height - e.Y));
            var box = new Box(click - add, click + add);

            // Get the first element that intersects with the box, selecting or deselecting as needed
            return Document.Map.WorldSpawn.GetAllNodesIntersecting2DLineTest(box).FirstOrDefault();
        }

        /// <summary>
        /// If the mouse is single-clicked in a 2D viewport, select the closest element that is under the cursor
        /// </summary>
        /// <param name="viewport">The 2D viewport</param>
        /// <param name="e">The mouse event</param>
        protected override void LeftMouseClick(Viewport2D viewport, MouseEventArgs e)
        {
            var seltest = SelectionTest(viewport, e);
            if (seltest != null)
            {
                var list = new[] { seltest };
                SetSelected(seltest.IsSelected ? list : null, seltest.IsSelected ? null : list, false, false);
                Document.UpdateSelectLists();
            }

            base.LeftMouseClick(viewport, e);
            SelectionChanged();
        }

        protected override void LeftMouseClickOnResizeHandle(Viewport2D viewport, MouseEventArgs e)
        {
            base.LeftMouseClickOnResizeHandle(viewport, e);

            if (_currentTool == null) return;

            if (KeyboardState.Ctrl)
            {
                var seltest = SelectionTest(viewport, e);
                if (seltest != null)
                {
                    var list = new[] { seltest };
                    SetSelected(seltest.IsSelected ? list : null, seltest.IsSelected ? null : list, false, false);
                    Document.UpdateSelectLists();
                    SelectionChanged();
                    return;
                }
            }

            // Cycle through active tools
            var idx = _tools.IndexOf(_currentTool);
            SetCurrentTool(_tools[(idx + 1) % _tools.Count]);
        }

        private Matrix4d? GetTransformMatrix(Viewport2D viewport, MouseEventArgs e)
        {
            if (_currentTool == null) return null;
            return State.Handle == ResizeHandle.Center
                       ? _tools.OfType<ResizeTool>().First().GetTransformationMatrix(viewport, e, State, Document)
                       : _currentTool.GetTransformationMatrix(viewport, e, State, Document);
        }

        protected override void LeftMouseUpResizing(Viewport2D viewport, MouseEventArgs e)
        {
            if (_currentTool == null)
            {
                base.LeftMouseUpResizing(viewport, e);
                return;
            }

            // Execute the transform on the selection
            var transformation = GetTransformMatrix(viewport, e);
            if (transformation.HasValue)
            {
                if (KeyboardState.Shift && State.Handle == ResizeHandle.Center)
                {
                    // Clone the selection
                    foreach (var clone in ClipboardManager.CloneFlatHeirarchy(Document, Document.Selection.GetSelectedObjects()))
                    {
                        clone.Parent = Document.Map.WorldSpawn;
                        Document.Map.WorldSpawn.Children.Add(clone);
                        clone.UpdateBoundingBox();
                    }
                }
                ExecuteTransform(_currentTool.GetTransformName(), CreateMatrixMultTransformation(transformation.Value));
            }
            Document.EndSelectionTransform();
            State.ActiveViewport = null;
            State.Action = BoxAction.Drawn;
            Transforming = false;

            SelectionChanged();
        }

        protected override void MouseDraggingToResize(Viewport2D viewport, MouseEventArgs e)
        {
            if (_currentTool == null)
            {
                base.MouseDraggingToResize(viewport, e);
                return;
            }

            State.Action = BoxAction.Resizing;
            CurrentTransform = GetTransformMatrix(viewport, e);
            if (!Transforming) // First drag event
            {
                Document.StartSelectionTransform();
                Transforming = true;
            }
            if (CurrentTransform.HasValue)
            {
                Document.SetSelectListTransform(CurrentTransform.Value);
            }
        }

        #endregion

        #region Box drawn cancel/confirm

        /// <summary>
        /// Once a box is confirmed, we select all element intersecting with the box (contained within if shift is down).
        /// </summary>
        /// <param name="viewport">The viewport that the box was confirmed in</param>
        public override void BoxDrawnConfirm(ViewportBase viewport)
        {
            // don't do anything if the current tool is not null
            if (_currentTool != null) return;

            Box boundingbox;
            if (GetSelectionBox(out boundingbox))
            {
                // If the shift key is down, select all brushes that are fully contained by the box
                // Otherwise, select all brushes that intersect with the box
                var nodes = KeyboardState.Shift
                                ? Document.Map.WorldSpawn.GetAllNodesContainedWithin(boundingbox).ToList()
                                : Document.Map.WorldSpawn.GetAllNodesIntersectingWith(boundingbox).ToList();
                SetSelected(null, nodes, false, false);
                Document.UpdateSelectLists();
            }
            base.BoxDrawnConfirm(viewport);
            SelectionChanged();
        }

        public override void BoxDrawnCancel(ViewportBase viewport)
        {
            // don't do anything if the current tool is not null
            if (_currentTool != null) return;

            base.BoxDrawnCancel(viewport);
            SelectionChanged();
        }

        #endregion

        #region Render

        protected override bool ShouldRenderResizeBox(Viewport2D viewport)
        {
            if (_currentTool != null)
            {
                return State.Action == BoxAction.ReadyToResize && State.Handle == ResizeHandle.Center;
            }
            return base.ShouldRenderResizeBox(viewport);
        }

        /// <summary>
        /// Returns true if the handles should be rendered, false otherwise
        /// </summary>
        /// <returns>Whether or not to draw the handles</returns>
        private bool ShouldRenderHandles()
        {
            return _currentTool != null
                   && State.Action != BoxAction.Resizing;
        }

        /// <summary>
        /// Render all the handles as squares or circles depending on class implementation
        /// </summary>
        /// <param name="viewport">The viewport to draw in</param>
        /// <param name="start">The start of the box</param>
        /// <param name="end">The end of the box</param>
        private void RenderHandles(Viewport2D viewport, Coordinate start, Coordinate end)
        {
            if (_currentTool == null) return;
            var circles = _currentTool.RenderCircleHandles;

            // Get the filtered list of handles, and convert them to vector locations
            var z = (double)viewport.Zoom;
            var handles = _currentTool.GetHandles(start, end, viewport.Zoom)
                .Where(x => _currentTool.FilterHandle(x.Item1))
                .Select(x => new Vector2d((double)x.Item2, (double)x.Item3))
                .ToList();

            // Draw the insides of the handles in white
            GL.Color3(Color.White);
            foreach (var handle in handles)
            {
                GL.Begin(BeginMode.Polygon);
                if (circles) GLX.Circle(handle, 4, z, loop: true);
                else GLX.Square(handle, 4, z, true);
                GL.End();
            }

            // Draw the borders of the handles in black
            GL.Color3(Color.Black);
            GL.Begin(BeginMode.Lines);
            foreach (var handle in handles)
            {
                if (circles) GLX.Circle(handle, 4, z);
                else GLX.Square(handle, 4, z);
            }
            GL.End();
        }

        protected override void Render2D(Viewport2D viewport)
        {
            if (_currentTool == null)
            {
                base.Render2D(viewport);
                return;
            }

            var start = viewport.Flatten(State.BoxStart);
            var end = viewport.Flatten(State.BoxEnd);

            Matrix4d mat;
            GL.GetDouble(GetPName.ProjectionMatrix, out mat);

            // If transforming in the viewport, push the matrix transformation to the stack
            if (viewport == State.ActiveViewport && State.Action == BoxAction.Resizing && CurrentTransform.HasValue)
            {
                start = viewport.Flatten(State.PreTransformBoxStart);
                end = viewport.Flatten(State.PreTransformBoxEnd);

                var dir = DisplayListGroup.GetMatrixFor(viewport.Direction);
                var inv = Matrix4d.Invert(dir);
                GL.MultMatrix(ref dir);
                var transform = CurrentTransform.Value;
                GL.MultMatrix(ref transform);
                GL.MultMatrix(ref inv);
            }

            if (ShouldDrawBox())
            {
                RenderBox(viewport, start, end);
            }

            if (ShouldRenderResizeBox(viewport))
            {
                RenderResizeBox(viewport, start, end);
            }

            // Restore the untransformed matrix
            GL.LoadMatrix(ref mat);

            if (ShouldRenderHandles())
            {
                RenderHandles(viewport, start, end);
            }
        }

        #endregion

        #region Transform stuff

        /// <summary>
        /// Runs the transform on all the currently selected objects
        /// </summary>
        /// <param name="transformationName">The name of the transformation</param>
        /// <param name="transform">The transformation to apply</param>
        private void ExecuteTransform(string transformationName, IUnitTransformation transform)
        {
            var objects = Document.Selection.GetSelectedObjects().Where(o => o.Parent == null || !o.Parent.IsSelected).ToList();
            var idg = new IDGenerator();
            var clones = objects.Select(x => x.Clone(idg));
            foreach (var o in objects)
            {
                o.Transform(transform);
            }
            var name = transformationName + " (" + objects.Count + " object" + (objects.Count == 1 ? "" : "s") + ")";
            var he = new HistoryEdit(name, clones, objects);
            Document.History.AddHistoryItem(he);
        }

        /// <summary>
        /// Convert a Matrix4d into a unit transformation object
        /// TODO: Move this somewhere better (extension method?)
        /// </summary>
        /// <param name="mat">The matrix to convert</param>
        /// <returns>The unit transformation representation of the matrix</returns>
        private IUnitTransformation CreateMatrixMultTransformation(Matrix4d mat)
        {
            var dmat = new[]
                           {
                               (decimal) mat.M11, (decimal) mat.M21, (decimal) mat.M31, (decimal) mat.M41,
                               (decimal) mat.M12, (decimal) mat.M22, (decimal) mat.M32, (decimal) mat.M42,
                               (decimal) mat.M13, (decimal) mat.M23, (decimal) mat.M33, (decimal) mat.M43,
                               (decimal) mat.M14, (decimal) mat.M24, (decimal) mat.M34, (decimal) mat.M44
                           };
            return new UnitMatrixMult(dmat);
        }

        #endregion
    }
}