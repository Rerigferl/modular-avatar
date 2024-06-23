﻿#region

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using nadena.dev.ndmf.rq;
using nadena.dev.ndmf.rq.unity.editor;
using UnityEngine;
using Object = UnityEngine.Object;

#endregion

namespace nadena.dev.modular_avatar.core.editor
{
    public class ShapeChangerPreview : IRenderFilter
    {
        public ReactiveValue<ImmutableList<RenderGroup>> TargetGroups { get; }
            = ReactiveValue<ImmutableList<RenderGroup>>.Create(
                "ShapeChangerPreview.TargetGroups", async ctx =>
                {
                    var allChangers =
                        await ctx.Observe(CommonQueries.GetComponentsByType<ModularAvatarShapeChanger>());

                    Dictionary<Renderer, ImmutableList<ModularAvatarShapeChanger>.Builder> groups =
                        new Dictionary<Renderer, ImmutableList<ModularAvatarShapeChanger>.Builder>(
                            new ObjectIdentityComparer<Renderer>());

                    foreach (var changer in allChangers)
                    {
                        // TODO: observe avatar root
                        ctx.Observe(changer);
                        if (!ctx.ActiveAndEnabled(changer)) continue;

                        var target = ctx.Observe(changer.targetRenderer.Get(changer));
                        var renderer = ctx.GetComponent<SkinnedMeshRenderer>(target);

                        if (renderer == null) continue;

                        if (!groups.TryGetValue(renderer, out var group))
                        {
                            group = ImmutableList.CreateBuilder<ModularAvatarShapeChanger>();
                            groups[renderer] = group;
                        }

                        group.Add(changer);
                    }

                    return groups.Select(g => RenderGroup.For(g.Key).WithData(g.Value.ToImmutable()))
                        .ToImmutableList();
                });

        public async Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var node = new Node();

            try
            {
                await node.Init(group, proxyPairs, context);
            }
            catch (Exception e)
            {
                // dispose
                throw;
            }

            return node;
        }

        private class Node : IRenderFilterNode
        {
            private Mesh _generatedMesh = null;
            private ImmutableList<ModularAvatarShapeChanger> _changers;

            private bool IsChangerActive(ModularAvatarShapeChanger changer, ComputeContext context)
            {
                if (changer == null) return false;

                if (context != null)
                {
                    return context.ActiveAndEnabled(changer);
                }
                else
                {
                    return changer.isActiveAndEnabled;
                }
            }

            public async Task Init(RenderGroup group, IEnumerable<(Renderer, Renderer)> renderers,
                ComputeContext context)
            {
                var (original, proxy) = renderers.First();

                if (original == null || proxy == null) return;
                if (!(proxy is SkinnedMeshRenderer smr)) return;

                _changers = group.GetData<ImmutableList<ModularAvatarShapeChanger>>();

                HashSet<int> toDelete = new HashSet<int>();
                var mesh = smr.sharedMesh;

                foreach (var changer in _changers)
                {
                    if (!IsChangerActive(changer, context)) continue;

                    foreach (var shape in changer.Shapes)
                    {
                        if (shape.ChangeType == ShapeChangeType.Delete)
                        {
                            var index = mesh.GetBlendShapeIndex(shape.ShapeName);
                            if (index < 0) continue;
                            toDelete.Add(index);
                        }
                    }
                }

                if (toDelete.Count > 0)
                {
                    mesh = Object.Instantiate(mesh);

                    var bsPos = new Vector3[mesh.vertexCount];
                    bool[] targetVertex = new bool[mesh.vertexCount];
                    foreach (var bs in toDelete)
                    {
                        int frames = mesh.GetBlendShapeFrameCount(bs);
                        for (int f = 0; f < frames; f++)
                        {
                            mesh.GetBlendShapeFrameVertices(bs, f, bsPos, null, null);

                            for (int i = 0; i < bsPos.Length; i++)
                            {
                                if (bsPos[i].sqrMagnitude > 0.0001f)
                                {
                                    targetVertex[i] = true;
                                }
                            }
                        }
                    }

                    List<int> tris = new List<int>();
                    for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    {
                        tris.Clear();

                        var baseVertex = (int)mesh.GetBaseVertex(subMesh);
                        mesh.GetTriangles(tris, subMesh, false);

                        for (int i = 0; i < tris.Count; i += 3)
                        {
                            if (targetVertex[tris[i] + baseVertex] || targetVertex[tris[i + 1] + baseVertex] ||
                                targetVertex[tris[i + 2] + baseVertex])
                            {
                                tris.RemoveRange(i, 3);
                                i -= 3;
                            }
                        }

                        mesh.SetTriangles(tris, subMesh, false, baseVertex: baseVertex);
                    }

                    smr.sharedMesh = mesh;
                    _generatedMesh = mesh;
                }
            }


            public RenderAspects Reads => RenderAspects.Shapes | RenderAspects.Mesh;
            public RenderAspects WhatChanged => RenderAspects.Shapes | RenderAspects.Mesh;

            public void Dispose()
            {
                if (_generatedMesh != null) Object.DestroyImmediate(_generatedMesh);
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (_changers == null) return; // can happen transiently as we disable the last component
                if (!(proxy is SkinnedMeshRenderer smr)) return;

                Mesh mesh;
                if (_generatedMesh != null)
                {
                    smr.sharedMesh = _generatedMesh;
                    mesh = _generatedMesh;
                }
                else
                {
                    mesh = smr.sharedMesh;
                }

                if (mesh == null) return;

                foreach (var changer in _changers)
                {
                    if (!IsChangerActive(changer, null)) continue;

                    foreach (var shape in changer.Shapes)
                    {
                        var index = mesh.GetBlendShapeIndex(shape.ShapeName);
                        if (index < 0) continue;

                        float setToValue = -1;

                        switch (shape.ChangeType)
                        {
                            case ShapeChangeType.Delete:
                                setToValue = 100;
                                break;
                            case ShapeChangeType.Set:
                                setToValue = shape.Value;
                                break;
                        }

                        smr.SetBlendShapeWeight(index, setToValue);
                    }
                }
            }
        }
    }
}