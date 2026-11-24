import sys
with open('src/LlmHarnessAndAgentEngine.cs', 'r') as f:
    content = f.read()

lines = content.split('\n')
new_lines = []
for line in lines:
    if 'Console.WriteLine("CHECKING DOOMLOOP' in line: continue
    if 'Console.WriteLine(" - "' in line: continue
    if 'Console.WriteLine("DOOMLOOP DETECTED' in line: continue
    new_lines.append(line)

with open('src/LlmHarnessAndAgentEngine.cs', 'w') as f:
    f.write('\n'.join(new_lines))
