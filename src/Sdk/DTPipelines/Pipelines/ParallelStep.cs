using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace GitHub.DistributedTask.Pipelines
{
    public class ParallelStep : JobStep
    {
        [JsonConstructor]
        public ParallelStep()
        {
        }

        private ParallelStep(ParallelStep ParallelStepToClone)
            : base(ParallelStepToClone)
        {
            if (ParallelStepToClone.m_steps?.Count > 0)
            {
                foreach (var step in ParallelStepToClone.m_steps)
                {
                    this.Steps.Add(step.Clone() as Step);
                }
            }

            if (ParallelStepToClone.m_outputs?.Count > 0)
            {
                this.m_outputs = new Dictionary<String, String>(ParallelStepToClone.m_outputs, StringComparer.OrdinalIgnoreCase);
            }
        }

        public override StepType Type => StepType.Parallel;

        public IList<Step> Steps
        {
            get
            {
                if (m_steps == null)
                {
                    m_steps = new List<Step>();
                }
                return m_steps;
            }
        }

        public IDictionary<String, String> Outputs
        {
            get
            {
                if (m_outputs == null)
                {
                    m_outputs = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
                }
                return m_outputs;
            }
        }

        public override Step Clone()
        {
            return new ParallelStep(this);
        }

        [OnSerializing]
        private void OnSerializing(StreamingContext context)
        {
            if (m_steps?.Count == 0)
            {
                m_steps = null;
            }

            if (m_outputs?.Count == 0)
            {
                m_outputs = null;
            }
        }

        [DataMember(Name = "Steps", EmitDefaultValue = false)]
        private IList<Step> m_steps;

        [DataMember(Name = "Outputs", EmitDefaultValue = false)]
        private IDictionary<String, String> m_outputs;
    }
}
