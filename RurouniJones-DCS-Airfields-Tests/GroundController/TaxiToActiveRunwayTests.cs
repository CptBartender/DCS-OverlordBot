﻿using Geo.Geometries;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using RurouniJones.DCS.Airfields.Controllers.Ground;

namespace RurouniJones.DCS.Airfields.Controllers.Tests
{
    [TestClass]
    public class TaxiToActiveRunwayTests
    {
        public static IEnumerable<object[]> TaxiScenarios()
        {
            yield return new object[] { new TaxiScenario("Al Dhafra AB", "Ramp 1", 310, new Point(24.244862290776, 54.562445823216), "Runway-3 1 Right", new List<string> { "Alfa" }) };
            yield return new object[] { new TaxiScenario("Al Dhafra AB", "Ramp 1", 170, new Point(24.244862290776, 54.562445823216), "Runway-1 3 Left", new List<string> { "Alfa", "Foxtrot" }) };

            yield return new object[] { new TaxiScenario("Al Dhafra AB", "Uniform Spots 1", 310, new Point(24.216944646008, 54.563235561423), "Runway-3 1 Left", new List<string> { "Uniform", "Uniform 0" }) };
            yield return new object[] { new TaxiScenario("Al Dhafra AB", "Uniform Spots 1", 10, new Point(24.216944646008, 54.563235561423), "Runway-3 1 Left", new List<string> { "Uniform", "Uniform 0" }) };
            
            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Apron 1", 90, new Point(45.0101581, 37.3481765), "Runway-0 4", new List<string> { "Mike", "Alpha" }) };
            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Echo Spots", 90, new Point(45.0094606, 37.3635130), "Runway-0 4", new List<string> { "Echo", "Delta", "Mike", "Alpha" }) };

            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Apron 1", -1, new Point(45.0101581, 37.3481765), "Runway-0 4", new List<string> { "Mike", "Alpha" }) };
            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Apron 1", 350, new Point(45.0101581, 37.3481765), "Runway-0 4", new List<string> { "Mike", "Alpha" }) };
            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Apron 1", 10, new Point(45.0101581, 37.3481765), "Runway-0 4", new List<string> { "Mike", "Alpha" }) };

            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Apron 1", 270, new Point(45.0101581, 37.3481765), "Runway-2 2", new List<string> { "Mike", "Delta" }) };
            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Echo Spots", 270, new Point(45.0094606, 37.3635130), "Runway-2 2", new List<string> { "Echo" }) };
            yield return new object[] { new TaxiScenario("Anapa-Vityazevo", "Echo Spots", 40, new Point(45.0094606, 37.3635130), "Runway-0 4", new List<string> { "Echo", "Delta", "Mike", "Alpha" }) };
            
            yield return new object[] { new TaxiScenario("Krasnodar-Center", "Echo Spots 1", 340, new Point(45.082339143765, 38.954220071576), "Runway-2 7", new List<string> { "Echo" }) };
            yield return new object[] { new TaxiScenario("Krasnodar-Center", "Echo Spots 1", 270, new Point(45.082339143765, 38.954220071576), "Runway-2 7", new List<string> { "Echo" }) };

            yield return new object[] { new TaxiScenario("Krasnodar-Pashkovsky", "Apron A", 90, new Point(45.038881971882, 39.14140614585), "Runway-0 5 Left", new List<string> { "Alfa" }) };
            yield return new object[] { new TaxiScenario("Krasnodar-Pashkovsky", "Apron 1", 90, new Point(45.047601257612, 39.200777416505), "Runway-0 5 Right", new List<string> { "November", "Echo" }) };

            yield return new object[] { new TaxiScenario("Mineralnye Vody", "Maintenance Area", 90, new Point(44.223130773545, 43.104834499253), "Runway-1 2", new List<string> { "November", "Echo", "Alfa" }) };

            yield return new object[] { new TaxiScenario("Maykop-Khanskaya", "Apron 2 1", 270, new Point(44.671288619807, 40.030771437314), "Runway-2 2", new List<string> { "Victor", "Echo"}) };

        }

        [DataTestMethod]
        [DynamicData(nameof(TaxiScenarios), DynamicDataSourceType.Method)]
        public void TaxiToActiveRunway(TaxiScenario scenario)
        {
            var airfield = Populator.Airfields.First(af => af.Name.Equals(scenario.Airfield));
            var controller = new GroundController(airfield);

            airfield.WindHeading = scenario.Wind;
            TaxiInstructions expected = new TaxiInstructions()
            {
                DestinationName = scenario.Destination,
                TaxiwayNames = scenario.Taxiways,
                Comments = new List<string>()
            };

            var actual = controller.GetTaxiInstructions(scenario.StartPoint);

            AssertInstructions(expected, actual);
        }

        public static void AssertInstructions(TaxiInstructions expected, TaxiInstructions actual)
        {
            StringAssert.Contains(expected.DestinationName, actual.DestinationName);
            CollectionAssert.AreEqual(expected.TaxiwayNames, actual.TaxiwayNames);
        }
        public class TaxiScenario
        {
            public string Airfield { get; }
            public string Source { get; }
            public int Wind { get; }
            public Point StartPoint { get; }
            public string Destination { get; }
            public List<string> Taxiways { get; }
            public List<string> Comments { get; } = new List<string>();

            public TaxiScenario(string airfield, string source, int wind, Point startPoint, string destination, List<string> taxiways)
            {
                Airfield = airfield;
                Source = source;
                Wind = wind;
                StartPoint = startPoint;
                Destination = destination;
                Taxiways = taxiways;
            }

            public override string ToString()
            {
                return $"{Airfield} (Wind {Wind:D3}): {Source} -> {Destination}";
            }
        }
    }
}
