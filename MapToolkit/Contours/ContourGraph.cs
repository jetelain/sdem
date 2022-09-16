﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClipperLib;
using GeoJSON.Text.Feature;
using GeoJSON.Text.Geometry;
using MapToolkit.DataCells;

namespace MapToolkit.Contours
{
    public class ContourGraph
    {
        private class LinesByElevation : Dictionary<int, List<ContourLine>>
        {
            public List<ContourLine> GetOrCreateElevation(int level)
            {
                if (!TryGetValue(level, out var lines))
                {
                    lines = new List<ContourLine>();
                    Add(level, lines);
                }
                return lines;
            }
        }

        private readonly double thresholdSqared = Coordinates.DefaultThresholdSquared;

        private readonly LinesByElevation linesByLevel = new LinesByElevation();

        public int Count => linesByLevel.Values.Sum(l => l.Count);

        public IEnumerable<ContourLine> Lines => linesByLevel.Values.SelectMany(l => l);

        private LinesByElevation AddSegments(IEnumerable<ContourSegment> segments, LinesByElevation prevScan)
        {
            var currentScan = new HashSet<ContourLine>();
            var currentScanIndex = new LinesByElevation();

            var unknownHypothesis = AddSegments(segments.Where(s => !s.Point1.AlmostEquals(s.Point2, thresholdSqared)), prevScan, currentScan, currentScanIndex);

            if (unknownHypothesis.Count > 0)
            {
                var remain = AddSegments(unknownHypothesis, prevScan, currentScan, currentScanIndex);
                var remain2 = AddSegments(remain, prevScan, currentScan, currentScanIndex);
                var remain3 = AddSegments(remain2, prevScan, currentScan, currentScanIndex);

                if (remain3.Count > 0)
                {
                    // Give up, keep first hypothesis
                    // might create some issues in generated graph
                    foreach (var x in remain3)
                    {
                        if (x.IsValidHypothesis)
                        {
                            x.ValidateHypothesis();
                        }
                    }
                    AddSegments(remain3, prevScan, currentScan, currentScanIndex);
                }
            }
            return currentScanIndex;
        }

        private List<ContourSegment> AddSegments(IEnumerable<ContourSegment> segments, LinesByElevation prevScan, HashSet<ContourLine> currentScan, LinesByElevation currentScanIndex)
        {
            var unknownHypothesis = new List<ContourSegment>();
            foreach (var segment in segments)
            {
                if (segment.IsValidHypothesis)
                {
                    var prevLine = AddSegment(segment, prevScan, currentScanIndex);
                    if (prevLine == null)
                    {
                        unknownHypothesis.Add(segment);
                    }
                    else if (currentScan.Add(prevLine))
                    {
                        currentScanIndex.GetOrCreateElevation((int)prevLine.Level).Add(prevLine);
                    }
                }
            }
            return unknownHypothesis;
        }

        private ContourLine? AddSegment(ContourSegment segment, LinesByElevation previousParallel, LinesByElevation currentParallel)
        {
            var segmentKey = (int)segment.Level;

            if (currentParallel.TryGetValue(segmentKey, out var currentLines))
            {
                var lastest = currentLines.AsEnumerable().Reverse()/*.Take(5)*/;
                foreach (var prev in lastest)
                {
                    if (prev.TryAdd(segment))
                    {
                        var merged = Merge(prev, lastest);
                        if (previousParallel.TryGetValue(segmentKey, out var prevLinesX))
                        {
                            return Merge(merged, prevLinesX);
                        }
                        return merged;
                    }
                }
            }
            if (previousParallel.TryGetValue(segmentKey, out var prevLines))
            {
                foreach (var line in prevLines)
                {
                    if (line.TryAdd(segment, thresholdSqared))
                    {
                        return Merge(line, prevLines);
                    }
                }
            }
            if (segment.IsHypothesis)
            {
                return null;
            }
            var newLine = new ContourLine(segment);
            linesByLevel.GetOrCreateElevation((int)segment.Level).Add(newLine);
            return newLine;
        }

        private ContourLine Merge(ContourLine editedLine, IEnumerable<ContourLine> parallel)
        {
            foreach (var line in parallel)
            {
                if (line != editedLine && line.TryMerge(editedLine, thresholdSqared))
                {
                    return line;
                }
            }
            return editedLine;
        }

        public void Add(IDemDataView cell, IContourLevelGenerator generator, bool closeLines = false, IProgress<double>? progress = null)
        {
            var prevScan = new LinesByElevation();
            var segments = new List<ContourSegment>();

            for (int lat = 0; lat < cell.PointsLat - 1; lat++)
            {
                DemDataPoint? southWest = null;
                DemDataPoint? northWest = null;
                segments.Clear();
                foreach (var point in cell.GetPointsOnParallel(lat, 0, cell.PointsLon).Zip(cell.GetPointsOnParallel(lat + 1, 0, cell.PointsLon), (south, north) => new { south, north }))
                {
                    var southEast = point.south;
                    var northEast = point.north;
                    if (southWest != null && northWest != null)
                    {
                        segments.AddRange(new ContourSquare(northWest, southWest, southEast, northEast).Segments(generator));
                    }
                    southWest = southEast;
                    northWest = northEast;
                }

                prevScan = AddSegments(segments, prevScan);

                progress?.Report((double)lat / (cell.PointsLat - 1) * 100d);
            }
            Cleanup();
            progress?.Report(100d);
#if DEBUG
            Simplify();
#endif
            if (closeLines)
            {
                CloseLines(cell);
            }
        }

        private void CloseLines(IDemDataView cell)
        {
            var edgeSW = cell.GetPointsOnParallel(0, 0, 1).First().Coordinates;
            var edgeNE = cell.GetPointsOnParallel(cell.PointsLat - 1, cell.PointsLon - 1, 1).First().Coordinates;
            CloseLines(Lines, edgeSW, edgeNE);
            Cleanup();
        }

        public void Cleanup()
        {
            Parallel.ForEach(linesByLevel.Values, lines =>
            {
                lines.RemoveAll(l => l.IsDiscarded && !l.IsSinglePoint);
            });
        }

#if DEBUG
        public void Simplify()
        {
            Cleanup();

            var initialCount = Count;
            var done = 0;
            var merged = 0;
            Parallel.ForEach(linesByLevel.Values, lines =>
            {
                var initial = lines.Count;
                var toKeepAsIs = lines.Where(l => l.IsClosed && !l.IsDiscarded).ToArray();
                var toAnalyse = lines.Where(l => !l.IsClosed).ToArray();
                for (var i = 0; i < toAnalyse.Length; ++i)
                {
                    var a = toAnalyse[i];
                    if (!a.IsClosed)
                    {
                        for (var j = 0; j < toAnalyse.Length; ++j)
                        {
                            if (i != j)
                            {
                                var b = toAnalyse[j];
                                if (!b.IsClosed)
                                {
                                    if (a.TryMerge(b, thresholdSqared))
                                    {
                                        Interlocked.Increment(ref merged);
                                    }
                                }
                            }
                        }
                    }
                }
                lines.Clear();
                lines.AddRange(toKeepAsIs);
                lines.AddRange(toAnalyse.Where(a => !a.IsDiscarded));
                var total = Interlocked.Add(ref done, initial);
            });
            if (merged > 0)
            {
                Console.WriteLine("Merged => " + merged);
            }
        }
#endif

        public IEnumerable<Polygon> ToPolygons(int rounding = -1, IProgress<double>? progress = null)
        {
            return linesByLevel.SelectMany(l => ToPolygons(l.Value, rounding, progress)).ToList();
        }

        private IEnumerable<Polygon> ToPolygons(List<ContourLine> value, int rounding, IProgress<double>? progress)
        {
            // FIXME : Very naive implementation that assumes that ouside world can't be part of polygons
            // we need to know edges positions to close open lines on edges.
            // we should use the anti clockwise/clockwise direction of lines to determine contours and holes.
            var clipper = new Clipper(progress);
            foreach (var line in value)
            {
                clipper.AddPath(line.Points.Select(p => p.ToIntPoint()).ToList(), PolyType.ptSubject, true);
            }
            var result = new PolyTree();
            clipper.Execute(ClipType.ctXor, result);
            return result.Childs
                .Select(c => new Polygon((new[] { ToLineString(c, rounding) })
                             .Concat(c.Childs.Select(h => ToLineString(h, rounding))))).ToList();
        }

        private static void CloseLines(IEnumerable<ContourLine> value, Coordinates edgeSW, Coordinates edgeNE)
        {
            var notClosed = value.Where(l => !l.IsClosed).ToList();
            if (notClosed.Count == 0)
            {
                return;
            }
            foreach (var line in notClosed)
            {
                if (line.IsClosed)
                {
                    continue;
                }
                if (line.First.Latitude == edgeNE.Latitude) // First On North, look EAST
                {
                    LookEast(edgeSW, edgeNE, notClosed, line, line.First);
                }
                else if (line.First.Longitude == edgeNE.Longitude) // First On East, look SOUTH
                {
                    LookSouth(edgeSW, edgeNE, notClosed, line, line.First);
                }
                else if (line.First.Latitude == edgeSW.Latitude) // First On South, look WEST
                {
                    LookWest(edgeSW, edgeNE, notClosed, line, line.First);
                }
                else if (line.First.Longitude == edgeSW.Longitude) // First On West, look North
                {
                    LookNorth(edgeSW, edgeNE, notClosed, line, line.First);
                }
            }
        }

        private static void LookEast(Coordinates edgeSW, Coordinates edgeNE, List<ContourLine> notClosed, ContourLine line, Coordinates lookFrom, int depth = 0)
        {
            var other = notClosed
                .Where(n => !n.IsClosed && n.Last.Latitude == edgeNE.Latitude && n.Last.Longitude > lookFrom.Longitude)
                .OrderBy(n => n.Last.Longitude)
                .FirstOrDefault();
            if (other != null)
            {
                other.Append(line);
            }
            else if ( depth < 4 )
            {
                line.Points.Insert(0, edgeNE);
                LookSouth(edgeSW, edgeNE, notClosed, line, edgeNE, depth + 1);
            }
        }

        private static void LookSouth(Coordinates edgeSW, Coordinates edgeNE, List<ContourLine> notClosed, ContourLine line, Coordinates lookFrom, int depth = 0)
        {
            var other = notClosed
                .Where(n => !n.IsClosed && n.Last.Longitude == edgeNE.Longitude && n.Last.Latitude < lookFrom.Latitude)
                .OrderByDescending(n => n.Last.Latitude)
                .FirstOrDefault();
            if (other != null)
            {
                other.Append(line);
            }
            else if (depth < 4)
            {
                var southEast = new Coordinates(edgeSW.Latitude, edgeNE.Longitude);
                line.Points.Insert(0, southEast);
                LookWest(edgeSW, edgeNE, notClosed, line, southEast, depth + 1);
            }
        }

        private static void LookWest(Coordinates edgeSW, Coordinates edgeNE, List<ContourLine> notClosed, ContourLine line, Coordinates lookFrom, int depth = 0)
        {
            var other = notClosed
                .Where(n => !n.IsClosed && n.Last.Latitude == edgeSW.Latitude && n.Last.Longitude < lookFrom.Longitude)
                .OrderByDescending(n => n.Last.Longitude)
                .FirstOrDefault();
            if (other != null)
            {
                other.Append(line);
            }
            else if (depth < 4)
            {
                line.Points.Insert(0, edgeSW);
                LookNorth(edgeSW, edgeNE, notClosed, line, edgeSW, depth + 1);
            }
        }

        private static void LookNorth(Coordinates edgeSW, Coordinates edgeNE, List<ContourLine> notClosed, ContourLine line, Coordinates lookFrom, int depth = 0)
        {
            var other = notClosed
                .Where(n => !n.IsClosed && n.Last.Longitude == edgeSW.Longitude && n.Last.Latitude > lookFrom.Latitude)
                .OrderBy(n => n.Last.Latitude)
                .FirstOrDefault();
            if (other != null)
            {
                other.Append(line);
            }
            else if (depth < 4)
            {
                var northWest = new Coordinates(edgeNE.Latitude, edgeSW.Longitude);
                line.Points.Insert(0, northWest);
                LookEast(edgeSW, edgeNE, notClosed, line, northWest, depth + 1);
            }
        }

        private static LineString ToLineString(PolyNode c, int rounding)
        {
            var points = c.Contour.Select(c => new Coordinates(c, rounding));
            return new LineString(points.Concat(points.Take(1)).ToList());
        }
    }
}
