﻿using System;
using System.Collections.Generic;
using System.Text;
using Pmad.Cartography.DataCells;
using System.Text.Json.Serialization;

namespace Pmad.Cartography.Databases
{
    public class DemDatabaseFileInfos
    {
        [JsonConstructor]
        public DemDatabaseFileInfos(string path, DemDataCellMetadata metadata)
        {
            Path = path; 
            Metadata = metadata;
        }

        public DemDatabaseFileInfos(string path, IDemDataCellMetadata metadata)
        {
            Path = path;
            Metadata = metadata as DemDataCellMetadata ?? new DemDataCellMetadata(metadata);
        }

        public string Path { get; }

        public DemDataCellMetadata Metadata { get; }
    }
}
