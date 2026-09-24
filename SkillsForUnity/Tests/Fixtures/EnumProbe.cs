using UnityEngine;

namespace UnitySkills.Tests.Fixtures
{
    /// <summary>Fixture: enum fields whose member indexes and values differ in the ways serialized enum writes must handle.</summary>
    public class EnumProbe : MonoBehaviour
    {
        [System.Flags]
        public enum ProbeFlags { None = 0, A = 1, B = 2, C = 4, D = 8 }

        public enum ProbeSparse { Low = 10, Mid = 20, High = 30 }

        public enum ProbeSeq { Zero, One, Two }

        public ProbeFlags flags;
        public ProbeSparse sparse = ProbeSparse.Low;
        public ProbeSeq seq;
    }
}

// Producer:Betsy
