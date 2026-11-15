using System.Runtime.CompilerServices;

// The package tests (Tests/Editor) check internal parts such as the dispatcher wrapper.
[assembly: InternalsVisibleTo("GameServer.Client.Tests")]
