﻿using System;
using System.Collections.Generic;
using Pmad.Geometry;
using Pmad.Geometry.Algorithms;
using Pmad.Geometry.Collections;

namespace Pmad.Cartography.Contours
{
    public sealed class ContourLine
    {
        private ReadOnlyArrayBuilder<CoordinatesValue> points = new ReadOnlyArrayBuilder<CoordinatesValue>();

        internal ContourLine(ContourSegment segment)
        {
#if DEBUG
            if (!segment.IsValidHypothesis)
            {
                throw new ArgumentException();
            }
#endif
            this.points.Add(segment.Point1);
            this.points.Add(segment.Point2);
            Level = segment.Level;
        }

        public ContourLine(IEnumerable<CoordinatesValue> points, double level)
        {
            this.points.AddRange(points);
            Level = level;
            UpdateIsClosed(Coordinates.LatLonSettings.NegligibleDistanceSquared);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>
        /// Points are counter clockwise for hills.
        /// 
        /// Points are clockwise for basin.
        /// </remarks>
        public ReadOnlyArrayBuilder<CoordinatesValue> Points => points;

        public double Level { get; }

        public CoordinatesValue First => points[0];

        public CoordinatesValue Last => points[points.Count-1];

        public bool IsClosed { get; private set; }

        public bool IsSinglePoint => IsClosed && Points.Count == 2;

        public bool IsDiscarded => IsClosed && Points.Count == 0;


        internal bool TryAdd(ContourSegment segment, double thresholdSqared)
        {
#if DEBUG
            if (!segment.IsValidHypothesis)
            {
                throw new ArgumentException();
            }
#endif
            if (IsClosed || segment.Level != Level)
            {
                return false;
            }
            if (segment.Point1.AlmostEquals(Last, thresholdSqared))
            {
                Points.Add(segment.Point2);
            }
            else if (segment.Point2.AlmostEquals(First, thresholdSqared))
            {
                Points.Prepend(segment.Point1);
            }
            else
            {
                return false;
            }
            UpdateIsClosed(thresholdSqared);
            segment.ValidateHypothesis();
            return true;
        }

        internal bool TryMerge(ContourLine other, double thresholdSqared)
        {
            if (IsClosed || other.IsClosed || other.Level != Level || other == this)
            {
                return false;
            }
            if (Last.AlmostEquals(other.First, thresholdSqared))
            {
                Points.AddRange(other.Points.Slice(1));
                other.Discard();
            }
            else if (First.AlmostEquals(other.Last, thresholdSqared))
            {
                other.Points.AddRange(Points.Slice(1));
                points = other.Points;
                other.Discard();
            }
            else
            {
                return false;
            }
            UpdateIsClosed(thresholdSqared);
            return true;
        }

        public void Append(ContourLine other, double thresholdSqared)
        {
            if (other == this)
            {
                Points.Add(First);
                UpdateIsClosed(thresholdSqared);
                return;
            }
            Points.AddRange(other.Points);
            other.Discard();
            UpdateIsClosed(thresholdSqared);
        }

        public void Close(double thresholdSqared)
        {
            UpdateIsClosed(thresholdSqared);
        }

        private void Discard()
        {
            IsClosed = true;
            points = new ReadOnlyArrayBuilder<CoordinatesValue>();
        }

        internal void UpdateIsClosed(double thresholdSqared)
        {
            if (Last.AlmostEquals(First, thresholdSqared))
            {
                IsClosed = true;
            }
        }

        public bool IsCounterClockWise => SignedArea<double, Vector2D>.GetSignedAreaD(points.AsSpan<CoordinatesValue,Vector2D>()) > 0;


        public bool IsPointInside(CoordinatesValue point)
        {
            return points.AsSpan<CoordinatesValue,Vector2D>().TestPointInPolygon(point.Vector2D) == Pmad.Geometry.Clipper2Lib.PointInPolygonResult.IsInside;
        }

        public bool IsPointInsideOrOnBoundary(CoordinatesValue point)
        {
            return points.AsSpan<CoordinatesValue, Vector2D>().TestPointInPolygon(point.Vector2D) != Pmad.Geometry.Clipper2Lib.PointInPolygonResult.IsOutside;
        }
    }
}
