﻿// Copyright (c) Microsoft. All rights reserved.

namespace SemanticKernel.IntegrationTests.Agents.CommonInterfaceConformance.AgentWithStatePartConformance;

public class OpenAIResponseAgentWithAIContextProviderTests() : AgentWithAIContextProviderTests<OpenAIResponseAgentFixture>(() => new OpenAIResponseAgentFixture())
{
}
