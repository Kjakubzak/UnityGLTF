using System.Runtime.CompilerServices;

// Exposes internal types (e.g. KhrCharacterBaker) to the test assembly for white-box golden tests.
[assembly: InternalsVisibleTo("UnityGLTF.KhrCharacter.Tests")]
