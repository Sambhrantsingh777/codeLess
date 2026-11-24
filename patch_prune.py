import sys
import re

with open('src/codeLess.Core/AgentContextBuilder.cs', 'r') as f:
    content = f.read()

target = '(?<=<tool_result[^>]*>)[\\s\\S]*?(?=</tool_result>)'
repl = '(<tool_result[^>]*>)[\\s\\S]*?(</tool_result>)'
content = content.replace(target, repl)

target_str = '"\\n[Old tool result content cleared]\\n"'
repl_str = '"$1\\n[Old tool result content cleared]\\n$2"'
content = content.replace(target_str, repl_str)

with open('src/codeLess.Core/AgentContextBuilder.cs', 'w') as f:
    f.write(content)

