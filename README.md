# IsBlittableGenerator

A Roslyn source generator that automatically adds an `IsBlittable` property to structs with `StructLayout` attribute.

## Description

`IsBlittableGenerator` is a .NET source generator that analyzes structs with `StructLayout` attribute and automatically adds a static `IsBlittable` property to determine if the struct is blittable. 

This is particularly useful for interop scenarios where you need to know if a struct can be safely marshaled between managed and unmanaged code.

## Features

- Automatically generates `IsBlittable` property for structs with `StructLayout` attribute
- Analyzes struct fields to determine blittability
- Generates a static dictionary of blittable types for quick lookup
- Supports partial structs
- Works with .NET Standard 2.0 and above

## Installation

```xml
<PackageReference Include="IsBlittableGenerator" Version="1.0.0" />
```

## Usage

1. Add the `StructLayout` attribute to your struct:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal partial struct MyStruct1
{
    public int Field1;
    public float Field2;
}

[StructLayout(LayoutKind.Sequential)]
internal partial struct MyStruct2
{
    public int Field1;
    public float Field2;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Field3;
}

[StructLayout(LayoutKind.Sequential)]
internal partial struct MyStruct3
{
    public int Field1;
    public MyStruct1 Field2;
}

[StructLayout(LayoutKind.Sequential)]
internal partial struct MyStruct4
{
    public int Field1;
    public MyStruct5 Field2;
}

internal partial struct MyStruct5
{
    public int Field1;
    public float Field2;
}
```

2. The generator will automatically add an `IsBlittable` property:

```csharp
internal partial struct MyStruct1
{
    public static bool IsBlittable
    {
        get
        {
            return true;
        }
    }
}

internal partial struct MyStruct2
{
    public static bool IsBlittable
    {
        get
        {
            return false;
        }
    }
}

internal partial struct MyStruct3
{
    public static bool IsBlittable
    {
        get
        {
            return true;
        }
    }
}

internal partial struct MyStruct4
{
    public static bool IsBlittable
    {
        get
        {
            return false;
        }
    }
}
```

3. You can also use the generated `BlittableTypes` class to check blittability:

```csharp
bool isBlittable = IsBlittableGenerator.BlittableTypes.IsBlittable<MyStruct1>();
```

For example:

```csharp
public unsafe static bool TryConvertTo<T>(IntPtr ptr, [MaybeNullWhen(false)] out T? value) where T : struct
{
    if(ptr == IntPtr.Zero)
    {
        value = null;
        return false;
    }

    if(IsBlittableGenerator.BlittableTypes.IsBlittable<T>())
    {
        value = System.Runtime.CompilerServices.Unsafe.AsRef<T>(ptr.ToPointer());
        return true;
    }

    value = Marshal.PtrToStructure<T>(ptr);
    return true;
}
```

PS: This is also the original intention of my writing this SG component

## Requirements

- .NET Standard 2.0 or later
- C# 9.0 or later

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
