using Godot;
using System;
using System.Collections.Generic;

public sealed class CreatureGenome
{
	public const int InputCount = 74;
	public const int HiddenCount = 24;
	public const int OutputCount = 36;

	public readonly float[] InputHiddenWeights;
	public readonly float[] HiddenBiases;
	public readonly float[] HiddenOutputWeights;
	public readonly float[] OutputBiases;

	public CreatureGenome()
	{
		InputHiddenWeights = new float[InputCount * HiddenCount];
		HiddenBiases = new float[HiddenCount];
		HiddenOutputWeights = new float[HiddenCount * OutputCount];
		OutputBiases = new float[OutputCount];
	}

	public CreatureGenome Clone()
	{
		CreatureGenome clone = new CreatureGenome();
		Array.Copy(InputHiddenWeights, clone.InputHiddenWeights, InputHiddenWeights.Length);
		Array.Copy(HiddenBiases, clone.HiddenBiases, HiddenBiases.Length);
		Array.Copy(HiddenOutputWeights, clone.HiddenOutputWeights, HiddenOutputWeights.Length);
		Array.Copy(OutputBiases, clone.OutputBiases, OutputBiases.Length);
		return clone;
	}

	public static CreatureGenome Randomize()
	{
		CreatureGenome genome = new CreatureGenome();
		FillRandom(genome.InputHiddenWeights);
		FillRandom(genome.HiddenBiases);
		FillRandom(genome.HiddenOutputWeights);
		FillRandom(genome.OutputBiases);
		return genome;
	}

	public static CreatureGenome Crossover(CreatureGenome parentA, CreatureGenome parentB)
	{
		CreatureGenome child = new CreatureGenome();
		Blend(parentA.InputHiddenWeights, parentB.InputHiddenWeights, child.InputHiddenWeights);
		Blend(parentA.HiddenBiases, parentB.HiddenBiases, child.HiddenBiases);
		Blend(parentA.HiddenOutputWeights, parentB.HiddenOutputWeights, child.HiddenOutputWeights);
		Blend(parentA.OutputBiases, parentB.OutputBiases, child.OutputBiases);
		return child;
	}

	public void Mutate(float rate, float amount)
	{
		MutateArray(InputHiddenWeights, rate, amount);
		MutateArray(HiddenBiases, rate, amount);
		MutateArray(HiddenOutputWeights, rate, amount);
		MutateArray(OutputBiases, rate, amount);
	}

	private static void FillRandom(float[] values)
	{
		for (int i = 0; i < values.Length; i++)
		{
			values[i] = (float)GD.RandRange(-1.0, 1.0);
		}
	}

	private static void Blend(float[] parentA, float[] parentB, float[] child)
	{
		for (int i = 0; i < child.Length; i++)
		{
			child[i] = GD.Randf() < 0.5f ? parentA[i] : parentB[i];
		}
	}

	private static void MutateArray(float[] values, float rate, float amount)
	{
		for (int i = 0; i < values.Length; i++)
		{
			if (GD.Randf() < rate)
			{
				values[i] += (float)GD.RandRange(-amount, amount);
			}
		}
	}
}

public sealed class CreatureBrain
{
	private readonly CreatureGenome _genome;

	public CreatureBrain(CreatureGenome genome)
	{
		_genome = genome ?? CreatureGenome.Randomize();
	}

	public float[] Evaluate(IReadOnlyList<float> inputs)
	{
		float[] hidden = new float[CreatureGenome.HiddenCount];
		float[] outputs = new float[CreatureGenome.OutputCount];

		for (int hiddenIndex = 0; hiddenIndex < CreatureGenome.HiddenCount; hiddenIndex++)
		{
			float sum = _genome.HiddenBiases[hiddenIndex];
			int inputOffset = hiddenIndex * CreatureGenome.InputCount;
			for (int inputIndex = 0; inputIndex < CreatureGenome.InputCount; inputIndex++)
			{
				float inputValue = inputIndex < inputs.Count ? inputs[inputIndex] : 0.0f;
				sum += inputValue * _genome.InputHiddenWeights[inputOffset + inputIndex];
			}

			hidden[hiddenIndex] = Mathf.Tanh(sum);
		}

		for (int outputIndex = 0; outputIndex < CreatureGenome.OutputCount; outputIndex++)
		{
			float sum = _genome.OutputBiases[outputIndex];
			int hiddenOffset = outputIndex * CreatureGenome.HiddenCount;
			for (int hiddenIndex = 0; hiddenIndex < CreatureGenome.HiddenCount; hiddenIndex++)
			{
				sum += hidden[hiddenIndex] * _genome.HiddenOutputWeights[hiddenOffset + hiddenIndex];
			}

			outputs[outputIndex] = Mathf.Tanh(sum);
		}

		return outputs;
	}
}

public readonly struct CreatureTraitModifiers
{
	public readonly float SightRangeMultiplier;
	public readonly float SmellRangeMultiplier;
	public readonly float FieldOfViewMultiplier;
	public readonly float MoveForceMultiplier;
	public readonly float StabilityMultiplier;

	public CreatureTraitModifiers(float sightRangeMultiplier, float smellRangeMultiplier, float fieldOfViewMultiplier, float moveForceMultiplier, float stabilityMultiplier)
	{
		SightRangeMultiplier = sightRangeMultiplier;
		SmellRangeMultiplier = smellRangeMultiplier;
		FieldOfViewMultiplier = fieldOfViewMultiplier;
		MoveForceMultiplier = moveForceMultiplier;
		StabilityMultiplier = stabilityMultiplier;
	}

	public static CreatureTraitModifiers Default => new CreatureTraitModifiers(1.0f, 1.0f, 1.0f, 1.0f, 1.0f);

	public static CreatureTraitModifiers FromTraits(IReadOnlyCollection<string> traits)
	{
		CreatureTraitModifiers modifiers = Default;
		if (traits == null)
		{
			return modifiers;
		}

		foreach (string trait in traits)
		{
			if (trait == "目がいい")
			{
				modifiers = new CreatureTraitModifiers(
					modifiers.SightRangeMultiplier * 1.65f,
					modifiers.SmellRangeMultiplier,
					modifiers.FieldOfViewMultiplier * 1.28f,
					modifiers.MoveForceMultiplier,
					modifiers.StabilityMultiplier
				);
			}
			else if (trait == "鼻がきく")
			{
				modifiers = new CreatureTraitModifiers(
					modifiers.SightRangeMultiplier,
					modifiers.SmellRangeMultiplier * 1.85f,
					modifiers.FieldOfViewMultiplier,
					modifiers.MoveForceMultiplier,
					modifiers.StabilityMultiplier
				);
			}
			else if (trait == "速い")
			{
				modifiers = new CreatureTraitModifiers(
					modifiers.SightRangeMultiplier,
					modifiers.SmellRangeMultiplier,
					modifiers.FieldOfViewMultiplier,
					modifiers.MoveForceMultiplier * 1.35f,
					modifiers.StabilityMultiplier
				);
			}
			else if (trait == "力が強い")
			{
				modifiers = new CreatureTraitModifiers(
					modifiers.SightRangeMultiplier,
					modifiers.SmellRangeMultiplier,
					modifiers.FieldOfViewMultiplier,
					modifiers.MoveForceMultiplier * 1.18f,
					modifiers.StabilityMultiplier * 1.1f
				);
			}
		}

		return modifiers;
	}
}
