# Preweek Technical Documentation

## Technical Goal

The goal for Week 1 was to understand the different approaches to building AI coding agents and compare how each architecture fits different use cases. The focus was on exploring plain agents, agent skills, subagents, the Claude Agent SDK, and n8n to understand their roles, strengths, and trade-offs.

## Technical Uncertainty

I was unsure when a simple agent is sufficient versus when skills, subagents, or an SDK become necessary. I also wanted to understand whether workflow tools like n8n are suitable for coding tasks or are better suited to business process automation.

## Technical Observations

Plain agents are the simplest way to provide instructions and context, making them suitable for straightforward tasks. Agent skills help organize reusable capabilities that can be shared across projects, while subagents allow work to be delegated to specialized agents, improving modularity for more complex workflows. The Claude Agent SDK provides greater control over orchestration, tool usage, and agent behavior through code, making it appropriate for more advanced applications. In contrast, n8n focuses on orchestrating workflows between services and APIs rather than acting as a dedicated coding agent.

## Technical Conclusions

Each approach addresses a different level of complexity. Plain agents work well for simple tasks, skills improve reusability, subagents help manage specialized responsibilities, and the Agent SDK offers the flexibility needed for building customized agent systems. n8n complements these approaches by automating workflows rather than replacing coding agents.

## Key Takeaway

There is no single best agent architecture. The right approach depends on the complexity of the problem and the level of control required. Understanding these architectural patterns provides a solid foundation before implementing more advanced coding agents in the coming weeks.