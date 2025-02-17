﻿using PortingAssistant.Client.Model;
using PortingAssistant.Common.Model;

namespace PortingAssistant.Common.Listener
{
    public delegate void OnApiAnalysisUpdate(Response<ProjectApiAnalysisResult, SolutionProject> response);
}
