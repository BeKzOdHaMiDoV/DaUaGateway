using System;
using System.Linq;
using Opc.Ua;
using TitaniumAS.Opc.Client.Da;

namespace DaUaGateway.Da
{
    /// <summary>Описание тега для UA-стороны.</summary>
    public sealed class TagInfo
    {
        public string ItemId;
        public string[] Path;     // папки над тегом
        public string Name;       // имя тега в UA
        public NodeId DataType;
        public int ValueRank;
        public bool Writable;
        public bool Failed;       // DA-сервер отверг тег
        public string DaType;     // тип тега в DA (для списка тегов)
    }

    /// <summary>DA Quality → UA StatusCode.</summary>
    public static class QualityMapper
    {
        public static StatusCode ToStatusCode(OpcDaQuality q)
        {
            switch (q.Master)
            {
                case OpcDaQualityMaster.Good: return StatusCodes.Good;
                case OpcDaQualityMaster.Uncertain: return StatusCodes.Uncertain;
            }
            // точная причина Bad — SCADA увидит её в статусе тега
            switch (q.Status)
            {
                case OpcDaQualityStatus.BadCommFailure: return StatusCodes.BadCommunicationError;
                case OpcDaQualityStatus.BadNotConnected: return StatusCodes.BadNotConnected;
                case OpcDaQualityStatus.BadDeviceFailure: return StatusCodes.BadDeviceFailure;
                case OpcDaQualityStatus.BadSensorFailure: return StatusCodes.BadSensorFailure;
                case OpcDaQualityStatus.BadConfigurationError: return StatusCodes.BadConfigurationError;
                case OpcDaQualityStatus.BadOutOfService: return StatusCodes.BadOutOfService;
                case OpcDaQualityStatus.BadLastKnown: return StatusCodes.BadNoCommunication;
                default: return StatusCodes.Bad;
            }
        }
    }

    /// <summary>Значения COM → то, что умеет кодировать UA.</summary>
    public static class ValueConverter
    {
        public static object ToUa(object value)
        {
            switch (value)
            {
                case null: return null;
                case DBNull _: return null;
                case decimal d: return (double)d;                     // VT_CY
                case decimal[] da: return da.Select(x => (double)x).ToArray();
                default: return value;
            }
        }
    }

    /// <summary>Канонический тип DA (.NET Type) → DataType и ValueRank в UA.</summary>
    public static class TypeMapper
    {
        public static void Map(Type t, out NodeId dataType, out int valueRank)
        {
            dataType = DataTypeIds.BaseDataType;
            valueRank = ValueRanks.Any;
            if (t == null || t == typeof(object)) return;

            if (t == typeof(decimal)) { dataType = DataTypeIds.Double; valueRank = ValueRanks.Scalar; return; }
            if (t == typeof(decimal[])) { dataType = DataTypeIds.Double; valueRank = ValueRanks.OneDimension; return; }

            try
            {
                var ti = global::Opc.Ua.TypeInfo.Construct(t);
                if (ti == null || ti.BuiltInType == BuiltInType.Null || ti.BuiltInType == BuiltInType.Variant) return;
                var id = global::Opc.Ua.TypeInfo.GetDataTypeId(ti);
                if (NodeId.IsNull(id)) return;
                dataType = id;
                valueRank = ti.ValueRank;
            }
            catch
            {
                // неизвестный тип — оставляем BaseDataType
            }
        }
    }
}
