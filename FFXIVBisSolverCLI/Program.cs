﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFXIVBisSolver;
using Microsoft.Extensions.CommandLineUtils;
using org.gnu.glpk;
using OPTANO.Modeling.Common;
using OPTANO.Modeling.Optimization;
using OPTANO.Modeling.Optimization.Configuration;
using OPTANO.Modeling.Optimization.Enums;
using OPTANO.Modeling.Optimization.Solver.GLPK;
using OPTANO.Modeling.Optimization.Solver.Gurobi702;
using OPTANO.Modeling.Optimization.Solver.Z3;
using SaintCoinach;
using SaintCoinach.Ex;
using SaintCoinach.Xiv;
using SaintCoinach.Xiv.Items;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FFXIVBisSolverCLI
{
    public class Program
    {
        //TODO: make this more pretty
        public static void Main(string[] args)
        {
            var cliApp = new CommandLineApplication();
            var xivPathOpt = cliApp.Option("-p |--game-path <pathToFFXIV>",
                "Path to the FFXIV game install (folder containing boot and game)", CommandOptionType.SingleValue);

            var configOpt = cliApp.Option("-c |--config-path <pathToYaml>",
                "Path to configuration YAML file, default to config.yaml", CommandOptionType.SingleValue);

            var excludedOpt = cliApp.Option("-X |--exclude <itemId>",
                "Item ids of gear or food to exclude from solving; repeat for non-unique items", CommandOptionType.MultipleValue);

            var requiredOpt = cliApp.Option("-R |--require <itemId>",
                "Item ids of items required when solving", CommandOptionType.MultipleValue);

            var minIlvlOpt = cliApp.Option("-m |--min-itemlevel <ilvl>",
                "Minimum item level of items to consider. Uses max-20 if not passed.", CommandOptionType.SingleValue);
            var maxIlvlOpt = cliApp.Option("-M |--max-itemlevel <ilvl>",
                "Maximum item level of items to consider", CommandOptionType.SingleValue);

            var maxOvermeldTierOpt = cliApp.Option("-T |--max-overmeld-tier <tier>",
                "The max tier of materia allowed for overmelds", CommandOptionType.SingleValue);

            var noMaximizeUnweightedOpt = cliApp.Option("--no-maximize-unweighted",
                "Choose to disable maximizing unweighted stats (usually accuracy). Shouldn't be needed.",
                CommandOptionType.NoValue);

            var noFoodOpt = cliApp.Option("--no-food", "Disable food", CommandOptionType.NoValue);

            var noMateriaOpt = cliApp.Option("--no-materia", "Disable materia", CommandOptionType.NoValue);

            var noRelicOpt = cliApp.Option("--no-relic", "Disable relic", CommandOptionType.NoValue);

            var tiersOpt = cliApp.Option("--use-tiers", "Enable SS tiers. Warning: slow unless using a commercial solver", CommandOptionType.NoValue);

            var outputOpt = cliApp.Option("-o |--output <file>", "Write output to <file>", CommandOptionType.SingleValue);

            var solverOpt = cliApp.Option("-s |--solver <solver>", "Solver to use (default: GLPK)",
                CommandOptionType.SingleValue);

            var noSolveOpt = cliApp.Option("--no-solve", "Don't solve the model; only works in conjunction with --debug", CommandOptionType.NoValue);

            var debugOpt = cliApp.Option("-d |--debug", "Print the used models in the current directory as model.lp",
                CommandOptionType.NoValue);

            var jobArg = cliApp.Argument("<job>", "Enter the job abbreviation to solve for");

            cliApp.HelpOption("-h |--help");

            cliApp.OnExecute(() =>
            {
                if (jobArg.Value == null)
                {
                    Console.Error.WriteLine("You must provide a job to solve for.");
                    return 1;
                }

                if (!xivPathOpt.HasValue())
                {
                    Console.Error.WriteLine("You must provide a path to FFXIV!");
                    return 1;
                }

                var realm = new ARealmReversed(xivPathOpt.Value(), Language.English);
                var xivColl = realm.GameData;

                //TODO: can combine those converters
                var deserializer = new DeserializerBuilder()
                    .WithTypeConverter(new BaseParamConverter(xivColl))
                    .WithTypeConverter(new ClassJobConverter(xivColl))
                    .WithTypeConverter(new EquipSlotConverter(xivColl))
                    .WithTypeConverter(new ItemConverter(xivColl))
                    .WithTypeConverter(new PiecewiseLinearConverter())
                    .WithNamingConvention(new CamelCaseNamingConvention())
                    .Build();

                SolverConfig solverConfig = null;

                using (var s = new FileStream(configOpt.HasValue() ? configOpt.Value() : "config.yaml", FileMode.Open))
                {
                    solverConfig = deserializer.Deserialize<SolverConfig>(new StreamReader(s));
                }

                solverConfig.MaximizeUnweightedValues = !noMaximizeUnweightedOpt.HasValue();
                solverConfig.UseTiers = tiersOpt.HasValue();

                var classJob = xivColl.GetSheet<ClassJob>().Single(x => string.Equals(x.Abbreviation, jobArg.Value,StringComparison.InvariantCultureIgnoreCase));

                var items = xivColl.GetSheet<Item>().ToList();

                if (excludedOpt.HasValue())
                {
                    var excludedIds = new List<int>();
                    foreach (var excluded in excludedOpt.Values)
                    {
                        try
                        {
                            var id = int.Parse(excluded);
                            var item = xivColl.Items[id];
                            excludedIds.Add(id);
                        }
                        catch (KeyNotFoundException)
                        {
                            Console.Error.WriteLine($"Unknown id {excluded}, ignoring.");
                        }
                        catch (FormatException)
                        {
                            Console.Error.WriteLine($"Not an integer: {excluded}");
                        }
                        catch (OverflowException)
                        {
                            Console.Error.WriteLine($"Too large: {excluded}");
                        }
                    }
                    items = items.Where(k => !excludedIds.Contains(k.Key)).ToList();
                }
                
                //TODO: duplicated code
                if (requiredOpt.HasValue())
                {
                    solverConfig.RequiredItems = new List<int>();
                    requiredOpt.Values.Select(int.Parse).ForEach(solverConfig.RequiredItems.Add);

                }

                var equip = items.OfType<Equipment>().Where(e => e.ClassJobCategory.ClassJobs.Contains(classJob));
                
                var maxIlvl = equip.Max(x => x.ItemLevel.Key);
                if (maxIlvlOpt.HasValue())
                {
                    maxIlvl = int.Parse(maxIlvlOpt.Value());
                }

                var minIlvl = maxIlvl - 20;
                if (minIlvlOpt.HasValue())
                {
                    minIlvl = int.Parse(minIlvlOpt.Value());
                }

                equip = equip.Where(e => e.ItemLevel.Key >= minIlvl && e.ItemLevel.Key <= maxIlvl || solverConfig.RequiredItems!= null && solverConfig.RequiredItems.Contains(e.Key)).ToList();

                var food = noFoodOpt.HasValue() ? new List<FoodItem>() : items.Where(FoodItem.IsFoodItem).Select(t => new FoodItem(t));

                var maxTier = items.OfType<MateriaItem>().Max(i => i.Tier);
                var materia = noMateriaOpt.HasValue() ? new Dictionary<MateriaItem, bool>() : items.OfType<MateriaItem>()
                    .Where(i => i.Tier == maxTier || (maxOvermeldTierOpt.HasValue() && i.Tier == int.Parse(maxOvermeldTierOpt.Value()) - 1))
                    .ToDictionary(i => i, i => !maxOvermeldTierOpt.HasValue() || i.Tier < int.Parse(maxOvermeldTierOpt.Value()));

                if (noRelicOpt.HasValue())
                {
                    solverConfig.RelicConfigs = new Dictionary<int, RelicConfig>();
                }

                //TODO: improve solver handling
                SolverBase solver = CreateGLPKSolver();
                if (solverOpt.HasValue())
                {
                    switch (solverOpt.Value())
                    {
                        case "Gurobi":
                            solver = new GurobiSolver();
                            solverConfig.SolverSupportsSOS = true;
                            break;

                    }
                }

                var debug = debugOpt.HasValue();
                var settings = new OptimizationConfigSection();
                //settings.ModelElement.EnableFullNames = debug;

                using (var scope = new ModelScope(settings))
                {
                    var model = new BisModel(solverConfig, classJob,
                        equip, food, materia);

                    if (debug)
                    {
                        using (var f = new FileStream("model.lp", FileMode.Create))
                        {
                            model.Model.Write(f, FileType.LP);
                        }
                        using (var f = new FileStream("model.mps", FileMode.Create))
                        {
                            model.Model.Write(f, FileType.MPS);
                        }

                        if (noSolveOpt.HasValue())
                        {
                            Console.WriteLine("Printed model, exiting...");
                            return 0;
                        }
                    }

                    var solution = solver.Solve(model.Model);

                    model.ApplySolution(solution);

                    if (outputOpt.HasValue())
                    {
                        using (var fs = new FileStream(outputOpt.Value(), FileMode.Create))
                        {
                            var sw = new StreamWriter(fs);
                            OutputModel(model,sw);
                            sw.Close();
                        }
                    }
                    else
                    {
                        OutputModel(model, Console.Out);
                    }
                    Console.WriteLine(solverConfig.UseTiers ? "SS tiers have been taken into account" : "SS tiers have been ignored; pass --use-tiers to enable (slow)");
                }

                return 0;
            });

            cliApp.Execute(args);
        }

        private static GLPKSolver CreateGLPKSolver()
        {
            //TODO heuristics
            //TODO better GLPK configuration
            GLPKSolver solver = new GLPKSolver();

            return solver;
        }

        private static void OutputModel(BisModel model, TextWriter writer)
        {
            writer.WriteLine("Gear: ");
            model.ChosenGear.ForEach(
                kv =>
                    writer.WriteLine("\t" + string.Join(" or ", kv.EquipSlotCategory.PossibleSlots.OrderBy(s => s.ToString())) +
                                      ": " + kv));

            if (model.ChosenMateria.Any())
            {
                writer.WriteLine("Materia: ");
                model.ChosenMateria.ForEach(
                    kv =>
                        writer.WriteLine("\t" + kv.Item1 + ": " + kv.Item2 + ",\n\t\t - Materia: " + kv.Item3 +
                                          "\n\t\t\t   Amount: " + kv.Item4));
            }

            if (model.ChosenRelicStats.Any())
            {
                writer.WriteLine("Relic distribution: ");
                model.ChosenRelicDistribution.ForEach(
                    kv => writer.WriteLine("\t" + kv.Item1 + " - " + kv.Item2 + ": " + kv.Item3));
                writer.WriteLine("Relic stats: ");
                model.ChosenRelicStats.ForEach(kv => writer.WriteLine("\t" + kv.Item1 + " - " + kv.Item2 + ": " + kv.Item3));
            }

            if (model.ChosenFood != null)
            {
                writer.WriteLine("Food: ");
                writer.WriteLine("\t" + model.ChosenFood);
            }
            writer.WriteLine("Allocated stats: ");
            model.ResultAllocatableStats.ForEach(kv => writer.WriteLine("\t" + kv.Key + ": " + kv.Value));
            writer.WriteLine("Result stats with food:");
            model.ResultTotalStats.ForEach(kv => writer.WriteLine("\t" + kv.Key + ": " + kv.Value));
            writer.WriteLine($"Result stat weight: {model.ResultWeight}");
        }
    }
}
