# Full Duplex Pipe implementation for InterProcess Communication

This project demonstrates a complete implementation of a full duplex pipe for process intercommunication.


## Usage

```bash
NamedPipeServer server = new NamedPipeServer();
server.StartListening();
await server.SendMessageAsync("Message from server side");
```

```bash
NamedPipeClient client = new NamedPipeClient();
client.StartListening();
client.SendMessageAsync("Message from client side");
```		
		
### Prerequisites

- **Platform**: Windows/Unix.
- **Development Environment**: Visual Studio or any other C# development environment (.NET Core 6+).
- **Dependencies**: None other than standard .NET Core libraries.

### Important Notes

**Mind that this is not safe to use in production or any other environment without adding further protections etc.**

Using pipes in the wrong way has lead to big issues like in July 19, 2024.

Always mind that Privilege Escalation exists, when using pipes.
