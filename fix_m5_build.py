import sys
import re

with open('src/codeLess.Core/AgentContextBuilder.cs', 'r') as f:
    content = f.read()

content = content.replace("namespace codeLess.Core", "namespace codeLess.Studio")

with open('src/codeLess.Core/AgentContextBuilder.cs', 'w') as f:
    f.write(content)

with open('src/LlmHarnessAndAgentEngine.cs', 'r') as f:
    content = f.read()

content = content.replace("vendor.Family ?? vendor.Id", "vendor.Id")
content = content.replace("activeVendor.ContextWindow > 0", "false")
content = content.replace("activeVendor.ContextWindow", "128000")
content = content.replace("activeVendor.MaxOutputTokens > 0", "false")
content = content.replace("activeVendor.MaxOutputTokens", "4096")

with open('src/LlmHarnessAndAgentEngine.cs', 'w') as f:
    f.write(content)
