// Copyright 1998-2014 Epic Games, Inc. All Rights Reserved.

#include "../ShaderCompilerCommon.h"
//@todo-rco: Remove STL!
#include "../hlslcc.h"
#include "../hlslcc_private.h"
#include "../Metal/MetalBackend.h"
#include "../glsl/ir_gen_glsl.h"
#include "../mesa/compiler.h"
#include "../mesa/glsl_parser_extras.h"
#include "../mesa/hash_table.h"
#include "../mesa/ir_rvalue_visitor.h"
#include "../PackUniformBuffers.h"
#include "../IRDump.h"
#include "../OptValueNumbering.h"
#include "../mesa/ir_optimization.h"
#include "../Metal/MetalUtils.h"
#include <algorithm>


const bool bExpandVSInputsToFloat4 = false;
const bool bGenerateVSInputDummies = false;

static int GetIndexSuffix(const char* Prefix, int PrefixLength, const char* Semantic)
{
	check(Semantic);

	if (!strncmp(Semantic, Prefix, PrefixLength))
	{
		Semantic += PrefixLength;
		int Index = 0;
		if (isdigit(*Semantic))
		{
			Index = (*Semantic) - '0';

			++Semantic;
			if (*Semantic == 0)
			{
				return Index;
			}
			else if (isdigit(*Semantic))
			{
				Index = Index * 10 + (*Semantic) - '0';
				++Semantic;
				if (*Semantic == 0)
				{
					return Index;
				}
			}
		}
	}

	return -1;
}

static int GetAttributeIndex(const char* Semantic)
{
	return GetIndexSuffix("ATTRIBUTE", 9, Semantic);
}

static int GetInAttributeIndex(const char* Semantic)
{
	return GetIndexSuffix("in_ATTRIBUTE", 12, Semantic);
}

const glsl_type* PromoteHalfToFloatType(_mesa_glsl_parse_state* state, const glsl_type* type)
{
	if (type->base_type == GLSL_TYPE_HALF)
	{
		return glsl_type::get_instance(GLSL_TYPE_FLOAT, type->vector_elements, type->matrix_columns);
	}
	else if (type->is_array())
	{
		auto* ElementType = type->element_type();
		auto* NewElementType = PromoteHalfToFloatType(state, ElementType);
		if (NewElementType != ElementType)
		{
			return glsl_type::get_array_instance(NewElementType, type->length);
		}
	}
	else if (type->is_record())
	{
		auto* Fields = ralloc_array(state, glsl_struct_field, type->length);
		bool bNeedNewType = false;
		for (unsigned i = 0; i < type->length; ++i)
		{
			auto* NewMemberType = PromoteHalfToFloatType(state, type->fields.structure[i].type);
			Fields[i] = type->fields.structure[i];
			if (NewMemberType != type->fields.structure[i].type)
			{
				bNeedNewType = true;
				Fields[i].type = NewMemberType;
			}
		}

		if (bNeedNewType)
		{
			auto* NewType = glsl_type::get_record_instance(Fields, type->length, ralloc_asprintf(state, "%s_F", type->name));
			// Hack: This way we tell this is a uniform buffer and we need to emit 'packed_'
			((glsl_type*)NewType)->HlslName = "__PACKED__";
			state->AddUserStruct(NewType);
			return NewType;
		}
	}

	return type;
}

void CreateNewAssignmentsFloat2Half(_mesa_glsl_parse_state* State, exec_list& NewAssignments, ir_variable* NewVar, ir_rvalue* RValue)
{
	if (NewVar->type->is_matrix())
	{
		for (int i = 0; i < NewVar->type->matrix_columns; ++i)
		{
			auto* NewF2H = new(State)ir_expression(ir_unop_f2h, new(State)ir_dereference_array(RValue, new(State)ir_constant(i)));
			auto* NewAssignment = new(State)ir_assignment(new(State)ir_dereference_array(NewVar, new(State)ir_constant(i)), NewF2H);
			NewAssignments.push_tail(NewAssignment);
		}
	}
	else
	{
		auto* NewF2H = new(State)ir_expression(ir_unop_f2h, RValue);
		auto* NewAssignment = new(State)ir_assignment(new(State)ir_dereference_variable(NewVar), NewF2H);

		NewAssignments.push_tail(NewAssignment);
	}
}

static void CreateNewAssignmentsHalf2Float(_mesa_glsl_parse_state* State, exec_list& NewAssignments, ir_variable* NewVar, ir_rvalue* RValue)
{
	if (NewVar->type->is_matrix())
	{
		for (int i = 0; i < NewVar->type->matrix_columns; ++i)
		{
			auto* NewF2H = new(State)ir_expression(ir_unop_h2f, new(State)ir_dereference_array(RValue, new(State)ir_constant(i)));
			auto* NewAssignment = new(State)ir_assignment(new(State)ir_dereference_array(NewVar, new(State)ir_constant(i)), NewF2H);
			NewAssignments.push_tail(NewAssignment);
		}
	}
	else
	{
		auto* NewF2H = new(State)ir_expression(ir_unop_h2f, RValue);
		auto* NewAssignment = new(State)ir_assignment(new(State)ir_dereference_variable(NewVar), NewF2H);

		NewAssignments.push_tail(NewAssignment);
	}
}

struct FFixIntrinsicsVisitor : public ir_rvalue_visitor
{
	_mesa_glsl_parse_state* State;
	bool bUsesFramebufferFetchES2;
	ir_variable* DestColorVar;
	FFixIntrinsicsVisitor(_mesa_glsl_parse_state* InState) :
		State(InState),
		bUsesFramebufferFetchES2(false),
		DestColorVar(nullptr)
	{
	}

	//ir_visitor_status visit_leave(ir_expression* expr) override
	virtual void handle_rvalue(ir_rvalue** RValue)
	{
		if (!RValue || !*RValue)
		{
			return;
		}
		auto* expr = (*RValue)->as_expression();
		if (!expr)
		{
			return;
		}

		ir_expression_operation op = expr->operation;

		if (op == ir_binop_mul && expr->type->is_matrix()
			&& expr->operands[0]->type->is_matrix()
			&& expr->operands[1]->type->is_matrix())
		{
			// Convert matrixCompMult to memberwise multiply
			check(expr->operands[0]->type == expr->operands[1]->type);
			auto* NewTemp = new(State)ir_variable(expr->operands[0]->type, nullptr, ir_var_temporary);
			base_ir->insert_before(NewTemp);
			for (int Index = 0; Index < expr->operands[0]->type->matrix_columns; ++Index)
			{
				auto* NewMul = new(State)ir_expression(ir_binop_mul,
					new(State)ir_dereference_array(expr->operands[0], new(State)ir_constant(Index)),
					new(State)ir_dereference_array(expr->operands[1], new(State)ir_constant(Index)));
				auto* NewAssign = new(State)ir_assignment(
					new(State)ir_dereference_array(NewTemp, new(State)ir_constant(Index)),
					NewMul);
				base_ir->insert_before(NewAssign);
			}

			*RValue = new(State)ir_dereference_variable(NewTemp);
		}
	}

	virtual ir_visitor_status visit_leave(ir_call* IR) override
	{
		if (IR->use_builtin && !strcmp(IR->callee_name(), FRAMEBUFFER_FETCH_ES2))
		{
			// 'Upgrade' framebuffer fetch
			check(IR->actual_parameters.is_empty());
			bUsesFramebufferFetchES2 = true;
			if (!DestColorVar)
			{
				// Generate new input variable for Metal semantics
				DestColorVar = new(State)ir_variable(glsl_type::get_instance(GLSL_TYPE_FLOAT, 4, 1), "gl_LastFragData", ir_var_in);
				DestColorVar->semantic = "gl_LastFragData";
			}

			auto* Assignment = new (State)ir_assignment(IR->return_deref, new(State)ir_dereference_variable(DestColorVar));
			IR->insert_before(Assignment);
			IR->remove();
		}

		return visit_continue;
	}
};

void FMetalCodeBackend::FixIntrinsics(exec_list* ir, _mesa_glsl_parse_state* state)
{
	ir_function_signature* MainSig = GetMainFunction(ir);
	check(MainSig);

	FFixIntrinsicsVisitor Visitor(state);
	Visitor.run(&MainSig->body);

	if (Visitor.bUsesFramebufferFetchES2)
	{
		check(Visitor.DestColorVar);
		MainSig->parameters.push_tail(Visitor.DestColorVar);
	}
}

struct FConvertUBVisitor : public ir_rvalue_visitor
{
	_mesa_glsl_parse_state* State;
	TStringIRVarMap& Map;
	FConvertUBVisitor(_mesa_glsl_parse_state* InState, TStringIRVarMap& InMap) :
		State(InState),
		Map(InMap)
	{
	}

	virtual void handle_rvalue(ir_rvalue** RValuePtr) override
	{
		if (!RValuePtr || !*RValuePtr)
		{
			return;
		}
		auto* ReferencedVar = (*RValuePtr)->variable_referenced();
		if (ReferencedVar && ReferencedVar->mode == ir_var_uniform && ReferencedVar->semantic)
		{
			auto FoundIter = Map.find(ReferencedVar->semantic);
			if (FoundIter != Map.end())
			{
				auto* StructVar = FoundIter->second;
				StructVar->used = 1;

				// Actually replace the variable
				auto* DeRefVar = (*RValuePtr)->as_dereference_variable();
				if (DeRefVar)
				{
					*RValuePtr = new(State)ir_dereference_record(StructVar, ReferencedVar->name);
				}
				else
				{
					check(0);
				}
			}
		}
	}
};

void FMetalCodeBackend::MovePackedUniformsToMain(exec_list* ir, _mesa_glsl_parse_state* state, FBuffers& OutBuffers)
{
	//IRDump(ir);
	TStringIRVarMap CBVarMap;

	// Now make a new struct type and global variable per uniform buffer
	for (int i = 0; i < state->num_uniform_blocks; ++i)
		//	for (auto& CB : state->CBuffersOriginal)
	{
		auto* CBP = state->FindCBufferByName(false, state->uniform_blocks[i]->name);
		check(CBP);
		auto& CB = *CBP;
		if (!CB.Members.empty())
		{
			glsl_struct_field* Fields = ralloc_array(state, glsl_struct_field, (unsigned)CB.Members.size());
			int Index = 0;
			for (auto& Member : CB.Members)
			{
				check(Member.Var);
				Fields[Index++] = glsl_struct_field(Member.Var->type, ralloc_strdup(state, Member.Var->name));
			}

			auto* Type = glsl_type::get_record_instance(Fields, (unsigned)CB.Members.size(), ralloc_asprintf(state, "CB_%s", CB.Name.c_str()));
			// Hack: This way we tell this is a uniform buffer and we need to emit 'packed_'
			((glsl_type*)Type)->HlslName = "__PACKED__";
			state->AddUserStruct(Type);

			auto* Var = new(state)ir_variable(Type, ralloc_asprintf(state, "%s", CB.Name.c_str()), ir_var_uniform);
			CBVarMap[CB.Name] = Var;
		}
	}

	FConvertUBVisitor ConvertVisitor(state, CBVarMap);
	ConvertVisitor.run(ir);

	std::set<const glsl_type*> PendingTypes;
	std::set<const glsl_type*> ProcessedTypes;

	// Actually only save the used variables
	for (auto& Pair : CBVarMap)
	{
		auto* Var = Pair.second;
		if (Var->used)
		{
			// Go through each struct type and mark it as packed
			ir->push_head(Var);
			if (Var->type->is_record())
			{
				PendingTypes.insert(Var->type);
			}
		}
	}

	// Mark all structures as packed
	while (!PendingTypes.empty())
	{
		auto* Type = *PendingTypes.begin();
		PendingTypes.erase(PendingTypes.begin());
		if (ProcessedTypes.find(Type) == ProcessedTypes.end())
		{
			ProcessedTypes.insert(Type);
			((glsl_type*)Type)->HlslName = "__PACKED__";

			for (int i = 0; i < Type->length; ++i)
			{
				if (Type->fields.structure[i].type->is_record())
				{
					PendingTypes.insert(Type->fields.structure[i].type);
				}
			}
		}
	}

	ir_function_signature* MainSig = GetMainFunction(ir);
	check(MainSig);

	// Gather all globals still lying outside Main
	foreach_iter(exec_list_iterator, iter, *ir)
	{
		auto* Instruction = ((ir_instruction*)iter.get());
		auto* Var = Instruction->as_variable();
		if (Var)
		{
			check(Var->mode == ir_var_uniform || Var->mode == ir_var_out || Var->mode == ir_var_in);
			OutBuffers.Buffers.Add(Var);
		}
	}
	OutBuffers.SortBuffers();

	// And move them to main
	for (auto Iter : OutBuffers.Buffers)
	{
		auto* Var = (ir_variable*)Iter;
		if (Var)
		{
			Var->remove();
			MainSig->parameters.push_tail(Var);
		}
	}
	//IRDump(ir, state);
}


void FMetalCodeBackend::PromoteInputsAndOutputsGlobalHalfToFloat(exec_list* Instructions, _mesa_glsl_parse_state* State, EHlslShaderFrequency Frequency)
{
	//IRDump(Instructions);
	ir_function_signature* EntryPointSig = GetMainFunction(Instructions);
	check(EntryPointSig);
	foreach_iter(exec_list_iterator, Iter, *Instructions)
	{
		ir_instruction* IR = (ir_instruction*)Iter.get();
		auto* Variable = IR->as_variable();
		if (Variable)
		{
			auto* NewType = PromoteHalfToFloatType(State, Variable->type);
			if (Variable->type == NewType)
			{
				continue;
			}

			switch (Variable->mode)
			{
			case ir_var_in:
			{
							  auto* NewVar = new(State)ir_variable(NewType, Variable->name, ir_var_in);
							  NewVar->semantic = Variable->semantic;
							  Variable->insert_before(NewVar);
							  Variable->name = nullptr;
							  Variable->semantic = nullptr;
							  Variable->mode = ir_var_temporary;
							  Variable->remove();
							  exec_list Assignments;
							  Assignments.push_head(Variable);
							  CreateNewAssignmentsFloat2Half(State, Assignments, Variable, new(State)ir_dereference_variable(NewVar));
							  EntryPointSig->body.get_head()->insert_before(&Assignments);
							  break;
			}
			case ir_var_out:
			{
							   if (Frequency != HSF_PixelShader)
							   {
								   auto* NewVar = new(State)ir_variable(NewType, Variable->name, ir_var_out);
								   NewVar->semantic = Variable->semantic;
								   Variable->insert_before(NewVar);
								   Variable->name = nullptr;
								   Variable->semantic = nullptr;
								   Variable->mode = ir_var_temporary;
								   Variable->remove();
								   exec_list Assignments;
								   CreateNewAssignmentsHalf2Float(State, Assignments, NewVar, new(State)ir_dereference_variable(Variable));
								   EntryPointSig->body.push_head(Variable);
								   EntryPointSig->body.append_list(&Assignments);
								   break;
							   }
			}
			}
		}
	}
	//IRDump(Instructions);
}

static bool ProcessStageInVariables(_mesa_glsl_parse_state* ParseState, EHlslShaderFrequency Frequency, ir_variable* Variable, TArray<glsl_struct_field>& OutStageInMembers, std::set<ir_variable*>& OutStageInVariables, unsigned int* OutVertexAttributesMask)
{
	if (Frequency == HSF_VertexShader)
	{
		// Generate an uber struct
		if (Variable->type->is_record())
		{
			check(0);
#if 0
			// Make sure all members have semantics
			std::list<const glsl_type*> PendingTypes;
			std::set<const glsl_type*> VisitedTypes;
			PendingTypes.push_back(Variable->type);
			while (!PendingTypes.empty())
			{
				auto* Type = PendingTypes.front();
				PendingTypes.pop_front();
				if (VisitedTypes.find(Type) == VisitedTypes.end())
				{
					VisitedTypes.insert(Type);
					bool bHasSemantic = true;
					for (int i = 0; i < Type->length; ++i)
					{
						auto& Member = Type->fields.structure[i];
						if (Member.type->is_record())
						{
							PendingTypes.push_back(Member.type);
						}
						else
						{
							if (Member.semantic)
							{
								int AttributeIndex = GetAttributeIndex(Member.semantic);
								if (AttributeIndex < 0)
								{
									_mesa_glsl_error(ParseState, "struct %s has a member with unknown semantic %s!\n", Type->name, Member.semantic);
									return false;
								}
								if (Member.type->is_array())
								{
									check(Member.type->element_type()->is_vector());
									for (int i = 0; i < Member.type->length; ++i, ++AttributeIndex)
									{
										glsl_struct_field OutMember;
										OutMember.type = Member.type->element_type();
										OutMember.semantic = ralloc_asprintf(ParseState, "ATTRIBUTE%d", AttributeIndex);
										OutMember.name = ralloc_asprintf(ParseState, "Attribute%d", AttributeIndex);

										if (OutVertexAttributesMask)
										{
											*OutVertexAttributesMask |= (1 << AttributeIndex);
										}

										OutStageInMembers.push_back(OutMember);
									}
								}
								else
								{
									glsl_struct_field OutMember;
									OutMember.type = Member.type;
									OutMember.semantic = ralloc_strdup(ParseState, Member.semantic);
									OutMember.name = ralloc_asprintf(ParseState, "Attribute%d", AttributeIndex);

									if (OutVertexAttributesMask)
									{
										*OutVertexAttributesMask |= (1 << AttributeIndex);
									}

									OutStageInMembers.push_back(OutMember);
								}
							}
							else
							{
								bHasSemantic = false;
								break;
							}
						}
						if (!bHasSemantic)
						{
							_mesa_glsl_error(ParseState, "struct %s has a member with no semantic!\n", Type->name);
							return false;
						}
					}
				}
			}
#endif
		}
		else
		{
			int AttributeIndex = GetInAttributeIndex(Variable->name);
			if (AttributeIndex >= 0)
			{
				if (Variable->type->is_array())
				{
					check(Variable->type->element_type()->is_vector());
					for (int i = 0; i < Variable->type->length; ++i, ++AttributeIndex)
					{
						glsl_struct_field OutMember;
						OutMember.type = Variable->type->element_type();
						OutMember.semantic = ralloc_asprintf(ParseState, "ATTRIBUTE%d", AttributeIndex);
						OutMember.name = ralloc_asprintf(ParseState, "ATTRIBUTE%d", AttributeIndex);

						if (OutVertexAttributesMask)
						{
							*OutVertexAttributesMask |= (1 << AttributeIndex);
						}

						OutStageInMembers.Add(OutMember);
					}
				}
				else
				{
					glsl_struct_field OutMember;
					OutMember.type = Variable->type;
					OutMember.semantic = ralloc_asprintf(ParseState, "ATTRIBUTE%d", AttributeIndex);
					OutMember.name = ralloc_asprintf(ParseState, "in_ATTRIBUTE%d", AttributeIndex);

					if (OutVertexAttributesMask)
					{
						*OutVertexAttributesMask |= (1 << AttributeIndex);
					}

					OutStageInMembers.Add(OutMember);
				}
			}
			else if (!strcmp(Variable->name, "gl_VertexID"))
			{
				glsl_struct_field OutMember;
				OutMember.type = Variable->type;
				OutMember.semantic = ralloc_asprintf(ParseState, "gl_VertexID");
				OutMember.name = ralloc_asprintf(ParseState, "gl_VertexID");
				OutStageInMembers.Add(OutMember);
			}
			else if (!strcmp(Variable->name, "gl_InstanceID"))
			{
				glsl_struct_field OutMember;
				OutMember.type = Variable->type;
				OutMember.semantic = ralloc_asprintf(ParseState, "gl_InstanceID");
				OutMember.name = ralloc_asprintf(ParseState, "gl_InstanceID");
				OutStageInMembers.Add(OutMember);
			}
			else
			{
				_mesa_glsl_error(ParseState, "Unknown semantic for input attribute %s!\n", Variable->name);
				check(0);
				return false;
			}
		}

		OutStageInVariables.insert(Variable);
		return true;
	}
	else
	{
		check(Frequency == HSF_PixelShader);
		if (!strcmp(Variable->name, "gl_FrontFacing"))
		{
			// Make sure we add a semantic
			Variable->semantic = "gl_FrontFacing";
			return true;
		}
	}

	/*
	if (!VerifyVariableHasSemantics(ParseState, Variable))
	{
	return false;
	}
	*/

	glsl_struct_field Member;
	Member.type = Variable->type;
	Member.name = ralloc_strdup(ParseState, Variable->name);
	Member.semantic = ralloc_strdup(ParseState, Variable->semantic ? Variable->semantic : Variable->name);
	OutStageInMembers.Add(Member);
	OutStageInVariables.insert(Variable);

	return true;
}


/** Information on system values. */
struct FSystemValue
{
	const char* Semantic;
	const glsl_type* Type;
	const char* GlslName;
	ir_variable_mode Mode;
	bool bOriginUpperLeft;
	bool bArrayVariable;
};

/** Vertex shader system values. */
static FSystemValue VertexSystemValueTable[] =
{
	{"SV_VertexID", glsl_type::int_type, "gl_VertexID", ir_var_in, false, false},
	{"SV_InstanceID", glsl_type::int_type, "gl_InstanceID", ir_var_in, false, false},
	//{ "SV_Position", glsl_type::vec4_type, "gl_Position", ir_var_out, false, true },
	{NULL, NULL, NULL, ir_var_auto, false, false}
};

/** Pixel shader system values. */
static FSystemValue PixelSystemValueTable[] =
{
	{"SV_Depth", glsl_type::float_type, "gl_FragDepth", ir_var_out, false, false},
	{"SV_Position", glsl_type::vec4_type, "gl_FragCoord", ir_var_in, true, false},
	//	{ "SV_IsFrontFace", glsl_type::bool_type, "gl_FrontFacing", ir_var_in, false, true },
	{"SV_PrimitiveID", glsl_type::int_type, "gl_PrimitiveID", ir_var_in, false, false},
	//	{ "SV_RenderTargetArrayIndex", glsl_type::int_type, "gl_Layer", ir_var_in, false, false },
	//	{ "SV_Target0", glsl_type::vec4_type, "gl_FragColor", ir_var_out, false, true },
	{NULL, NULL, NULL, ir_var_auto, false, false}
};

static FSystemValue* SystemValueTable[] =
{
	VertexSystemValueTable,
	PixelSystemValueTable,
	nullptr,
	nullptr,
	nullptr,
	nullptr
};

/**
* Generate an input semantic.
* @param Frequency - The shader frequency.
* @param ParseState - Parse state.
* @param Semantic - The semantic name to generate.
* @param Type - Value type.
* @param DeclInstructions - IR to which declarations may be added.
* @returns reference to IR variable for the semantic.
*/
static ir_rvalue* GenShaderInputSemantic(
	EHlslShaderFrequency Frequency,
	_mesa_glsl_parse_state* ParseState,
	const char* Semantic,
	const glsl_type* Type,
	exec_list* DeclInstructions,
	int SemanticArraySize,
	int SemanticArrayIndex)
{
	ir_variable* Variable = NULL;
	if (Semantic && strnicmp(Semantic, "SV_", 3) == 0)
	{
		FSystemValue* SystemValues = SystemValueTable[Frequency];
		for (int i = 0; SystemValues[i].Semantic != NULL; ++i)
		{
			if (SystemValues[i].Mode == ir_var_in
				//				&& (!SystemValues[i].bESOnly || ParseState->bGenerateES)
				&& stricmp(SystemValues[i].Semantic, Semantic) == 0)
			{
				check(0);
#if 0
				if (SystemValues[i].bArrayVariable)
				{
					// Built-in array variable. Like gl_in[x].gl_Position.
					// The variable for it has already been created in GenShaderInput().
					/*ir_variable**/ Variable = ParseState->symbols->get_variable("gl_in");
					check(Variable);
					ir_dereference_variable* ArrayDeref = new(ParseState)ir_dereference_variable(Variable);
					ir_dereference_array* StructDeref = new(ParseState)ir_dereference_array(
						ArrayDeref,
						new(ParseState)ir_constant((unsigned)SemanticArrayIndex)
						);
					ir_dereference_record* VariableDeref = new(ParseState)ir_dereference_record(
						StructDeref,
						SystemValues[i].GlslName
						);
					// TO DO - in case of SV_ClipDistance, we need to defer appropriate index in variable too.
					return VariableDeref;
				}
				else
				{
					// Built-in variable that shows up only once, like gl_FragCoord in fragment
					// shader, or gl_PrimitiveIDIn in geometry shader. Unlike gl_in[x].gl_Position.
					// Even in geometry shader input pass it shows up only once.

					// Create it on first pass, ignore the call on others.
					if (SemanticArrayIndex == 0)
					{
						ir_variable* Variable = new(ParseState)ir_variable(
							SystemValues[i].Type,
							SystemValues[i].GlslName,
							ir_var_in
							);
						Variable->read_only = true;
						Variable->origin_upper_left = SystemValues[i].bOriginUpperLeft;
						DeclInstructions->push_tail(Variable);
						ParseState->symbols->add_variable(Variable);
						ir_dereference_variable* VariableDeref = new(ParseState)ir_dereference_variable(Variable);

						return VariableDeref;
					}
					else
					{
						return NULL;
					}
				}
#endif
			}
		}
	}

	if (Variable)
	{
		// Up to this point, variables aren't contained in structs
		DeclInstructions->push_tail(Variable);
		ParseState->symbols->add_variable(Variable);
		Variable->centroid = false;// InputQualifier.Fields.bCentroid;
		Variable->interpolation = false;//InputQualifier.Fields.InterpolationMode;
		Variable->is_patch_constant = false;//InputQualifier.Fields.bIsPatchConstant;
		ir_rvalue* VariableDeref = new(ParseState)ir_dereference_variable(Variable);

		return VariableDeref;
	}

	// If we're here, no built-in variables matched.

	if (Semantic && strnicmp(Semantic, "SV_", 3) == 0)
	{
		_mesa_glsl_warning(ParseState, "unrecognized system "
			"value input '%s'", Semantic);
	}

	if (Frequency == HSF_VertexShader || ParseState->bGenerateES)
	{
		const char* Prefix = "in";
		if (ParseState->bGenerateES && Frequency == HSF_PixelShader)
		{
			Prefix = "var";
		}

		// Vertex shader inputs don't get packed into structs that we'll later morph into interface blocks
		if (ParseState->bGenerateES && Type->is_integer())
		{
			// Convert integer attributes to floats
			ir_variable* Variable = new(ParseState)ir_variable(
				Type,
				ralloc_asprintf(ParseState, "%s_%s_I", Prefix, Semantic),
				ir_var_temporary
				);
			Variable->centroid = false;//InputQualifier.Fields.bCentroid;
			Variable->interpolation = false;//InputQualifier.Fields.InterpolationMode;
			check(Type->is_vector() || Type->is_scalar());
			check(Type->base_type == GLSL_TYPE_INT || Type->base_type == GLSL_TYPE_UINT);

			// New float attribute
			ir_variable* ReplacedAttributeVar = new (ParseState)ir_variable(glsl_type::get_instance(GLSL_TYPE_FLOAT, Variable->type->vector_elements, 1), ralloc_asprintf(ParseState, "%s_%s", Prefix, Semantic), ir_var_in);
			ReplacedAttributeVar->read_only = true;
			ReplacedAttributeVar->centroid = false;//InputQualifier.Fields.bCentroid;
			ReplacedAttributeVar->interpolation = false;//InputQualifier.Fields.InterpolationMode;

			// Convert to integer
			ir_assignment* ConversionAssignment = new(ParseState)ir_assignment(
				new(ParseState)ir_dereference_variable(Variable),
				new(ParseState)ir_expression(
				Type->base_type == GLSL_TYPE_INT ? ir_unop_f2i : ir_unop_f2u,
				new (ParseState)ir_dereference_variable(ReplacedAttributeVar)
				)
				);

			DeclInstructions->push_tail(ReplacedAttributeVar);
			DeclInstructions->push_tail(Variable);
			DeclInstructions->push_tail(ConversionAssignment);
			ParseState->symbols->add_variable(Variable);
			ParseState->symbols->add_variable(ReplacedAttributeVar);

			ir_dereference_variable* VariableDeref = new(ParseState)ir_dereference_variable(ReplacedAttributeVar);
			return VariableDeref;
		}

		// Regular attribute
		Variable = new(ParseState)ir_variable(
			Type,
			ralloc_asprintf(ParseState, "%s_%s", Prefix, Semantic),
			ir_var_in
			);
		Variable->read_only = true;
		Variable->centroid = false;//InputQualifier.Fields.bCentroid;
		Variable->interpolation = false;//InputQualifier.Fields.InterpolationMode;
		Variable->is_patch_constant = false;//InputQualifier.Fields.bIsPatchConstant;

		DeclInstructions->push_tail(Variable);
		ParseState->symbols->add_variable(Variable);

		ir_dereference_variable* VariableDeref = new(ParseState)ir_dereference_variable(Variable);
		return VariableDeref;
	}
	else if (SemanticArrayIndex == 0)
	{
		// On first pass, create variable

		glsl_struct_field *StructField = ralloc_array(ParseState, glsl_struct_field, 1);

		memset(StructField, 0, sizeof(glsl_struct_field));
		StructField[0].type = Type;
		StructField[0].name = ralloc_strdup(ParseState, "Data");

		const glsl_type* VariableType = glsl_type::get_record_instance(StructField, 1, ralloc_strdup(ParseState, Semantic));
		if (SemanticArraySize)
		{
			// Pack it into an array too
			VariableType = glsl_type::get_array_instance(VariableType, SemanticArraySize);
		}

		ir_variable* Variable = new(ParseState)ir_variable(VariableType, ralloc_asprintf(ParseState, "in_%s", Semantic), ir_var_in);
		Variable->read_only = true;
		Variable->is_interface_block = true;
		Variable->centroid = false;//InputQualifier.Fields.bCentroid;
		Variable->interpolation = false;//InputQualifier.Fields.InterpolationMode;
		Variable->is_patch_constant = false;//InputQualifier.Fields.bIsPatchConstant;
		DeclInstructions->push_tail(Variable);
		ParseState->symbols->add_variable(Variable);

		ir_rvalue* VariableDeref = new(ParseState)ir_dereference_variable(Variable);
		if (SemanticArraySize)
		{
			// Deref inside array first
			VariableDeref = new(ParseState)ir_dereference_array(VariableDeref, new(ParseState)ir_constant((unsigned)SemanticArrayIndex)
				);
		}
		VariableDeref = new(ParseState)ir_dereference_record(VariableDeref, ralloc_strdup(ParseState, "Data"));
		return VariableDeref;
	}
	else
	{
		// Array variable, not first pass. It already exists, get it.
		ir_variable* Variable = ParseState->symbols->get_variable(ralloc_asprintf(ParseState, "in_%s", Semantic));
		check(Variable);

		ir_rvalue* VariableDeref = new(ParseState)ir_dereference_variable(Variable);
		VariableDeref = new(ParseState)ir_dereference_array(VariableDeref, new(ParseState)ir_constant((unsigned)SemanticArrayIndex));
		VariableDeref = new(ParseState)ir_dereference_record(VariableDeref, ralloc_strdup(ParseState, "Data"));
		return VariableDeref;
	}
}



/**
* Generate an input semantic.
* @param Frequency - The shader frequency.
* @param ParseState - Parse state.
* @param InputSemantic - The semantic name to generate.
* @param InputQualifier - Qualifiers applied to the semantic.
* @param InputVariableDeref - Deref for the argument variable.
* @param DeclInstructions - IR to which declarations may be added.
* @param PreCallInstructions - IR to which instructions may be added before the
*                              entry point is called.
*/
static void GenShaderInputForVariable(
	EHlslShaderFrequency Frequency,
	_mesa_glsl_parse_state* ParseState,
	const char* InputSemantic,
	ir_dereference* InputVariableDeref,
	exec_list* DeclInstructions,
	exec_list* PreCallInstructions,
	int SemanticArraySize,
	int SemanticArrayIndex
	)
{
	const glsl_type* InputType = InputVariableDeref->type;
	if (InputType->is_record())
	{
		check(0);
		/*
		for (int i = 0; i < InputType->length; ++i)
		{
		const char* FieldSemantic = InputType->fields.structure[i].semantic;
		const char* Semantic = 0;

		if (InputSemantic && FieldSemantic)
		{

		_mesa_glsl_warning(ParseState, "semantic '%s' of field '%s' will be overridden by enclosing types' semantic '%s'",
		InputType->fields.structure[i].semantic,
		InputType->fields.structure[i].name,
		InputSemantic);


		FieldSemantic = 0;
		}

		if (InputSemantic && !FieldSemantic)
		{
		Semantic = ralloc_asprintf(ParseState, "%s%d", InputSemantic, i);
		_mesa_glsl_warning(ParseState, "  creating semantic '%s' for struct field '%s'", Semantic, InputType->fields.structure[i].name);
		}
		else if (!InputSemantic && FieldSemantic)
		{
		Semantic = FieldSemantic;
		}
		else
		{
		Semantic = 0;
		}

		if (InputType->fields.structure[i].type->is_record() ||
		Semantic)
		{
		FSemanticQualifier Qualifier = InputQualifier;
		if (Qualifier.Packed == 0)
		{
		Qualifier.Fields.bCentroid = InputType->fields.structure[i].centroid;
		Qualifier.Fields.InterpolationMode = InputType->fields.structure[i].interpolation;
		Qualifier.Fields.bIsPatchConstant = InputType->fields.structure[i].patchconstant;
		}

		ir_dereference_record* FieldDeref = new(ParseState)ir_dereference_record(
		InputVariableDeref->clone(ParseState, NULL),
		InputType->fields.structure[i].name);
		GenShaderInputForVariable(
		Frequency,
		ParseState,
		Semantic,
		Qualifier,
		FieldDeref,
		DeclInstructions,
		PreCallInstructions,
		SemanticArraySize,
		SemanticArrayIndex
		);
		}
		else
		{
		_mesa_glsl_error(
		ParseState,
		"field '%s' in input structure '%s' does not specify a semantic",
		InputType->fields.structure[i].name,
		InputType->name
		);
		}
		}
		*/
	}
	else if (InputType->is_array())// || InputType->is_inputpatch() || InputType->is_outputpatch())
	{
		check(0);
		/*

		int BaseIndex = 0;
		const char* Semantic = 0;
		check(InputSemantic);
		ParseSemanticAndIndex(ParseState, InputSemantic, &Semantic, &BaseIndex);
		check(BaseIndex >= 0);
		check(InputType->is_array() || InputType->is_inputpatch() || InputType->is_outputpatch());
		const unsigned ElementCount = InputType->is_array() ? InputType->length : InputType->patch_length;

		{
		//check(!InputQualifier.Fields.bIsPatchConstant);
		InputQualifier.Fields.bIsPatchConstant = false;
		}

		for (unsigned i = 0; i < ElementCount; ++i)
		{
		ir_dereference_array* ArrayDeref = new(ParseState)ir_dereference_array(
		InputVariableDeref->clone(ParseState, NULL),
		new(ParseState)ir_constant((unsigned)i)
		);
		GenShaderInputForVariable(
		Frequency,
		ParseState,
		ralloc_asprintf(ParseState, "%s%d", Semantic, BaseIndex + i),
		InputQualifier,
		ArrayDeref,
		DeclInstructions,
		PreCallInstructions,
		SemanticArraySize,
		SemanticArrayIndex
		);
		}*/
	}
	else
	{
		check(!InputType->is_inputpatch() && !InputType->is_outputpatch());
		ir_rvalue* SrcValue = GenShaderInputSemantic(Frequency, ParseState, InputSemantic,
			InputType, DeclInstructions, SemanticArraySize,
			SemanticArrayIndex);
		if (SrcValue)
		{
			YYLTYPE loc = {0};
			apply_type_conversion(InputType, SrcValue, PreCallInstructions, ParseState, true, &loc);
			PreCallInstructions->push_tail(
				new(ParseState)ir_assignment(
				InputVariableDeref->clone(ParseState, NULL),
				SrcValue
				)
				);
		}
	}
}


/**
* Generate a shader input.
* @param Frequency - The shader frequency.
* @param ParseState - Parse state.
* @param InputSemantic - The semantic name to generate.
* @param InputQualifier - Qualifiers applied to the semantic.
* @param InputType - Value type.
* @param DeclInstructions - IR to which declarations may be added.
* @param PreCallInstructions - IR to which instructions may be added before the
*                              entry point is called.
* @returns the IR variable deref for the semantic.
*/
static ir_dereference_variable* GenerateShaderInput(
	EHlslShaderFrequency Frequency,
	_mesa_glsl_parse_state* ParseState,
	const char* InputSemantic,
	const glsl_type* InputType,
	exec_list* DeclInstructions,
	exec_list* PreCallInstructions)
{
	ir_variable* TempVariable = new(ParseState)ir_variable(
		InputType,
		NULL,
		ir_var_temporary);
	ir_dereference_variable* TempVariableDeref = new(ParseState)ir_dereference_variable(TempVariable);
	PreCallInstructions->push_tail(TempVariable);

	GenShaderInputForVariable(
		Frequency,
		ParseState,
		InputSemantic,
		TempVariableDeref,
		DeclInstructions,
		PreCallInstructions,
		0,
		0
		);
	return TempVariableDeref;
}


/**
* Generate an output semantic.
* @param Frequency - The shader frequency.
* @param ParseState - Parse state.
* @param Semantic - The semantic name to generate.
* @param Type - Value type.
* @param DeclInstructions - IR to which declarations may be added.
* @returns the IR variable for the semantic.
*/
static ir_rvalue* GenShaderOutputSemantic(
	EHlslShaderFrequency Frequency,
	_mesa_glsl_parse_state* ParseState,
	const char* Semantic,
	const glsl_type* Type,
	exec_list* DeclInstructions,
	const glsl_type** DestVariableType)
{
	FSystemValue* SystemValues = SystemValueTable[Frequency];
	ir_variable* Variable = NULL;

	if (Semantic && strnicmp(Semantic, "SV_", 3) == 0)
	{
		for (int i = 0; SystemValues[i].Semantic != NULL; ++i)
		{
			if (SystemValues[i].Mode == ir_var_out
				&& stricmp(SystemValues[i].Semantic, Semantic) == 0)
			{
				check(0);
				/*
				Variable = new(ParseState)ir_variable(
				SystemValues[i].Type,
				SystemValues[i].GlslName,
				ir_var_out
				);
				Variable->origin_upper_left = SystemValues[i].bOriginUpperLeft;*/
			}
		}
	}

	if (Variable == NULL && Frequency == HSF_VertexShader)
	{
		const int PrefixLength = 15;
		if (strnicmp(Semantic, "SV_ClipDistance", PrefixLength) == 0
			&& Semantic[PrefixLength] >= '0'
			&& Semantic[PrefixLength] <= '9')
		{
			check(0);
			/*
			int OutputIndex = Semantic[15] - '0';
			Variable = new(ParseState)ir_variable(
			glsl_type::float_type,
			ralloc_asprintf(ParseState, "gl_ClipDistance[%d]", OutputIndex),
			ir_var_out
			);*/
		}
	}

	if (Variable == NULL && Frequency == HSF_PixelShader)
	{
		const int PrefixLength = 9;
		if (strnicmp(Semantic, "SV_Target", PrefixLength) == 0
			&& Semantic[PrefixLength] >= '0'
			&& Semantic[PrefixLength] <= '7')
		{
			int OutputIndex = Semantic[PrefixLength] - '0';
			Variable = new(ParseState)ir_variable(
				Type,
				ralloc_asprintf(ParseState, "out_Target%d", OutputIndex),
				ir_var_out
				);
		}
	}

	if (Variable == NULL && ParseState->bGenerateES)
	{
		check(0);
		// Create a variable so that a struct will not get added
		Variable = new(ParseState)ir_variable(Type, ralloc_asprintf(ParseState, "var_%s", Semantic), ir_var_out);
	}

	if (Variable)
	{
		// Up to this point, variables aren't contained in structs
		*DestVariableType = Variable->type;
		DeclInstructions->push_tail(Variable);
		ParseState->symbols->add_variable(Variable);
		Variable->centroid = false;//OutputQualifier.Fields.bCentroid;
		Variable->interpolation = false;//OutputQualifier.Fields.InterpolationMode;
		Variable->is_patch_constant = false;// OutputQualifier.Fields.bIsPatchConstant;
		ir_rvalue* VariableDeref = new(ParseState)ir_dereference_variable(Variable);
		return VariableDeref;
	}

	if (Semantic && strnicmp(Semantic, "SV_", 3) == 0)
	{
		_mesa_glsl_warning(ParseState, "unrecognized system value output '%s'",
			Semantic);
	}

	*DestVariableType = Type;

	// Create variable
	glsl_struct_field *StructField = ralloc_array(ParseState, glsl_struct_field, 1);

	memset(StructField, 0, sizeof(glsl_struct_field));
	StructField[0].type = Type;
	StructField[0].name = ralloc_strdup(ParseState, "Data");

	const glsl_type* VariableType = glsl_type::get_record_instance(StructField, 1, ralloc_strdup(ParseState, Semantic));

	Variable = new(ParseState)ir_variable(VariableType, ralloc_asprintf(ParseState, "out_%s", Semantic), ir_var_out);
	Variable->centroid = false;//OutputQualifier.Fields.bCentroid;
	Variable->interpolation = false;//OutputQualifier.Fields.InterpolationMode;
	Variable->is_interface_block = true;
	Variable->is_patch_constant = false;//OutputQualifier.Fields.bIsPatchConstant;

	DeclInstructions->push_tail(Variable);
	ParseState->symbols->add_variable(Variable);

	ir_rvalue* VariableDeref = new(ParseState)ir_dereference_variable(Variable);
	VariableDeref = new(ParseState)ir_dereference_record(VariableDeref, ralloc_strdup(ParseState, "Data"));

	return VariableDeref;
}


/**
* Generate an output semantic.
* @param Frequency - The shader frequency.
* @param ParseState - Parse state.
* @param OutputSemantic - The semantic name to generate.
* @param OutputQualifier - Qualifiers applied to the semantic.
* @param OutputVariableDeref - Deref for the argument variable.
* @param DeclInstructions - IR to which declarations may be added.
* @param PostCallInstructions - IR to which instructions may be added after the
*                               entry point returns.
*/
void GenShaderOutputForVariable(
	EHlslShaderFrequency Frequency,
	_mesa_glsl_parse_state* ParseState,
	const char* OutputSemantic,
	ir_dereference* OutputVariableDeref,
	exec_list* DeclInstructions,
	exec_list* PostCallInstructions,
	int SemanticArraySize,
	int SemanticArrayIndex
	)
{
	const glsl_type* OutputType = OutputVariableDeref->type;
	if (OutputType->is_record())
	{
		check(0);
		/*
		for (int i = 0; i < OutputType->length; ++i)
		{
		const char* FieldSemantic = OutputType->fields.structure[i].semantic;
		const char* Semantic = 0;

		if (OutputSemantic && FieldSemantic)
		{

		_mesa_glsl_warning(ParseState, "semantic '%s' of field '%s' will be overridden by enclosing types' semantic '%s'",
		OutputType->fields.structure[i].semantic,
		OutputType->fields.structure[i].name,
		OutputSemantic);


		FieldSemantic = 0;
		}

		if (OutputSemantic && !FieldSemantic)
		{
		Semantic = ralloc_asprintf(ParseState, "%s%d", OutputSemantic, i);
		_mesa_glsl_warning(ParseState, "  creating semantic '%s' for struct field '%s'", Semantic, OutputType->fields.structure[i].name);
		}
		else if (!OutputSemantic && FieldSemantic)
		{
		Semantic = FieldSemantic;
		}
		else
		{
		Semantic = 0;
		}

		if (OutputType->fields.structure[i].type->is_record() ||
		Semantic
		)
		{
		// Dereference the field and generate shader outputs for the field.
		ir_dereference* FieldDeref = new(ParseState)ir_dereference_record(
		OutputVariableDeref->clone(ParseState, NULL),
		OutputType->fields.structure[i].name);
		GenShaderOutputForVariable(
		Frequency,
		ParseState,
		Semantic,
		FieldDeref,
		DeclInstructions,
		PostCallInstructions,
		SemanticArraySize,
		SemanticArrayIndex
		);
		}
		else
		{
		_mesa_glsl_error(
		ParseState,
		"field '%s' in output structure '%s' does not specify a semantic",
		OutputType->fields.structure[i].name,
		OutputType->name
		);
		}
		}
		*/
	}
	// TODO clean this up!!
	else if (OutputType->is_array())// || OutputType->is_outputpatch()))
	{
		check(0);
		/*
		if (OutputSemantic)
		{
		int BaseIndex = 0;
		const char* Semantic = 0;

		ParseSemanticAndIndex(ParseState, OutputSemantic, &Semantic, &BaseIndex);

		const unsigned ElementCount = OutputType->is_array() ? OutputType->length : (OutputType->patch_length);

		for (unsigned i = 0; i < ElementCount; ++i)
		{
		ir_dereference_array* ArrayDeref = new(ParseState)ir_dereference_array(
		OutputVariableDeref->clone(ParseState, NULL),
		new(ParseState)ir_constant((unsigned)i)
		);
		GenShaderOutputForVariable(
		Frequency,
		ParseState,
		ralloc_asprintf(ParseState, "%s%d", Semantic, BaseIndex + i),
		ArrayDeref,
		DeclInstructions,
		PostCallInstructions,
		SemanticArraySize,
		SemanticArrayIndex
		);
		}
		}
		else
		{
		_mesa_glsl_error(ParseState, "entry point does not specify a semantic for its return value");
		}
		*/
	}
	else
	{
		if (OutputSemantic)
		{
			YYLTYPE loc = {0};
			ir_rvalue* Src = OutputVariableDeref->clone(ParseState, NULL);
			const glsl_type* DestVariableType = NULL;
			ir_rvalue* DestVariableDeref = GenShaderOutputSemantic(Frequency, ParseState, OutputSemantic,
				OutputType, DeclInstructions, &DestVariableType);

			apply_type_conversion(DestVariableType, Src, PostCallInstructions, ParseState, true, &loc);
			PostCallInstructions->push_tail(new(ParseState)ir_assignment(DestVariableDeref, Src));
		}
		else
		{
			_mesa_glsl_error(ParseState, "entry point does not specify a semantic for its return value");
		}
	}
}


/**
* Generate an output semantic.
* @param Frequency - The shader frequency.
* @param ParseState - Parse state.
* @param OutputSemantic - The semantic name to generate.
* @param OutputQualifier - Qualifiers applied to the semantic.
* @param OutputType - Value type.
* @param DeclInstructions - IR to which declarations may be added.
* @param PreCallInstructions - IR to which isntructions may be added before the
entry point is called.
* @param PostCallInstructions - IR to which instructions may be added after the
*                               entry point returns.
* @returns the IR variable deref for the semantic.
*/
static ir_dereference_variable* GenerateShaderOutput(
	EHlslShaderFrequency Frequency,
	_mesa_glsl_parse_state* ParseState,
	const char* OutputSemantic,
	const glsl_type* OutputType,
	exec_list* DeclInstructions,
	exec_list* PreCallInstructions,
	exec_list* PostCallInstructions
	)
{
	// Generate a local variable to hold the output.
	ir_variable* TempVariable = new(ParseState)ir_variable(
		OutputType,
		NULL,
		ir_var_temporary);
	ir_dereference_variable* TempVariableDeref = new(ParseState)ir_dereference_variable(TempVariable);
	PreCallInstructions->push_tail(TempVariable);
	GenShaderOutputForVariable(
		Frequency,
		ParseState,
		OutputSemantic,
		TempVariableDeref,
		DeclInstructions,
		PostCallInstructions,
		0,
		0
		);
	return TempVariableDeref;
}


void FMetalCodeBackend::PackInputsAndOutputs(exec_list* Instructions, _mesa_glsl_parse_state* ParseState, EHlslShaderFrequency Frequency, exec_list& InputVars)
{
	ir_function_signature* EntryPointSig = GetMainFunction(Instructions);
	check(EntryPointSig);

	exec_list DeclInstructions;
	exec_list PreCallInstructions;
	exec_list ArgInstructions;
	exec_list PostCallInstructions;
	ParseState->symbols->push_scope();

	// Set of variables packed into a struct
	std::set<ir_variable*> VSStageInVariables;
	std::set<ir_variable*> PSStageInVariables;
	std::set<ir_variable*> VSOutVariables;
	ir_variable* VSOut = nullptr;
	ir_variable* PSOut = nullptr;
	ir_variable* VSStageIn = nullptr;
	std::map<std::string, glsl_struct_field> OriginalVSStageInMembers;
	ir_variable* PSStageIn = nullptr;
	if (Frequency == HSF_VertexShader)
	{
		// Vertex Fetch to Vertex connector
		TArray<glsl_struct_field> VSStageInMembers;

		// Vertex Output connector. Gather position semantic & other outputs into a struct
		TArray<glsl_struct_field> VSOutMembers;

		foreach_iter(exec_list_iterator, Iter, *Instructions)
		{
			ir_instruction* IR = (ir_instruction*)Iter.get();
			auto* Variable = IR->as_variable();
			if (Variable)
			{
				switch (Variable->mode)
				{
				case ir_var_out:
					{
						/*
						if (!VerifyVariableHasSemantics(ParseState, Variable))
						{
						return;
						}
						*/
						glsl_struct_field Member;
						Member.type = Variable->type;
						Member.name = ralloc_strdup(ParseState, Variable->name);
						Member.semantic = Variable->name;
						VSOutMembers.Add(Member);
						VSOutVariables.insert(Variable);
					}
					break;

				case ir_var_in:
					if (!ProcessStageInVariables(ParseState, Frequency, Variable, VSStageInMembers, VSStageInVariables, nullptr))
					{
						return;
					}
					break;
				}
			}
		}

		if (VSStageInMembers.Num())
		{
			check(Frequency == HSF_VertexShader);

			//@todo-rco: Make me nice...
			int AttributesUsedMask = 0;
			for (auto& Member : VSStageInMembers)
			{
				int Index = GetAttributeIndex(Member.semantic);
				if (Index >= 0 && Index < 16)
				{
					AttributesUsedMask |= (1 << Index);
				}
				InputVars.push_tail(new(ParseState)extern_var(new(ParseState)ir_variable(Member.type, ralloc_strdup(ParseState, Member.name), ir_var_in)));
			}

			if (bGenerateVSInputDummies)
			{
				int Bit = 0;
				for (int i = 0; i < 16; ++i)
				{
					if ((AttributesUsedMask & (1 << i)) == 0)
					{
						glsl_struct_field NewMember;
						NewMember.name = ralloc_asprintf(ParseState, "__dummy%d", i);
						NewMember.semantic = ralloc_asprintf(ParseState, "ATTRIBUTE%d", i);
						NewMember.type = glsl_type::get_instance(GLSL_TYPE_FLOAT, 4, 1);
						VSStageInMembers.Add(NewMember);
					}
				}
			}

			std::sort(VSStageInMembers.begin(), VSStageInMembers.end(),
				[](glsl_struct_field& A, glsl_struct_field& B)
				{
					return GetAttributeIndex(A.semantic) < GetAttributeIndex(B.semantic);
				});

			// Convert all members to float4
			if (bExpandVSInputsToFloat4)
			{
				for (auto& Member : VSStageInMembers)
				{
					OriginalVSStageInMembers[Member.name] = Member;
					check(Member.type->matrix_columns == 1);
					Member.type = glsl_type::get_instance(Member.type->base_type, 4, 1);
				}
			}

			auto* Type = glsl_type::get_record_instance(&VSStageInMembers[0], (unsigned int)VSStageInMembers.Num(), "FVSStageIn");
			VSStageIn = new(ParseState)ir_variable(Type, "__VSStageIn", ir_var_in);
			// Hack: This way we tell we need to convert types from half to float
			((glsl_type*)Type)->HlslName = "__STAGE_IN__";
			ParseState->symbols->add_variable(VSStageIn);

			if (!ParseState->AddUserStruct(Type))
			{
				YYLTYPE loc = {0};
				_mesa_glsl_error(&loc, ParseState, "struct '%s' previously defined", Type->name);
			}
			/*
			else
			{
			const glsl_type **NewList = reralloc(ParseState, ParseState->user_structures,
			const glsl_type *,
			ParseState->num_user_structures + 1);
			check(NewList);
			NewList[ParseState->num_user_structures] = Type;
			ParseState->user_structures = NewList;
			ParseState->num_user_structures++;
			}
			*/
		}

		if (VSOutMembers.Num())
		{
			auto* Type = glsl_type::get_record_instance(&VSOutMembers[0], (unsigned int)VSOutMembers.Num(), "FVSOut");
			VSOut = new(ParseState)ir_variable(Type, "__VSOut", ir_var_temporary);
			PostCallInstructions.push_tail(VSOut);
			ParseState->symbols->add_variable(VSOut);

			if (!ParseState->AddUserStruct(Type))
			{
				YYLTYPE loc = {0};
				_mesa_glsl_error(&loc, ParseState, "struct '%s' previously defined", Type->name);
			}
			/*
			else
			{
			const glsl_type **NewList = reralloc(ParseState, ParseState->user_structures,
			const glsl_type *,
			ParseState->num_user_structures + 1);
			check(NewList);
			NewList[ParseState->num_user_structures] = Type;
			ParseState->user_structures = NewList;
			ParseState->num_user_structures++;
			}
			*/
		}
	}
	else if (Frequency == HSF_PixelShader)
	{
		// Vertex to Pixel connector
		TArray<glsl_struct_field> PSStageInMembers;

		// Gather all inputs and generate the StageIn VS->PS connector
		foreach_iter(exec_list_iterator, Iter, *Instructions)
		{
			ir_instruction* IR = (ir_instruction*)Iter.get();
			auto* Variable = IR->as_variable();
			if (Variable)
			{
				switch (Variable->mode)
				{
				case ir_var_out:
					check(!PSOut);
					check(!Variable->type->is_record());
					check(!strcmp(Variable->name, "gl_FragColor"));
					PSOut = Variable;// new(ParseState)ir_variable(Variable->type, "gl_FragColor", ir_var_temporary);
					break;

				case ir_var_in:
					if (!ProcessStageInVariables(ParseState, Frequency, Variable, PSStageInMembers, PSStageInVariables, nullptr))
					{
						return;
					}
					break;

				default:
					break;
				}
			}
		}

		if (PSStageInMembers.Num())
		{
			auto* Type = glsl_type::get_record_instance(&PSStageInMembers[0], (unsigned int)PSStageInMembers.Num(), "FPSStageIn");
			// Hack: This way we tell we need to convert types from half to float
			((glsl_type*)Type)->HlslName = "__STAGE_IN__";
			PSStageIn = new(ParseState)ir_variable(Type, "__PSStageIn", ir_var_in);
			ParseState->symbols->add_variable(PSStageIn);

			if (!ParseState->AddUserStruct(Type))
			{
				YYLTYPE loc = {0};
				_mesa_glsl_error(&loc, ParseState, "struct '%s' previously defined", Type->name);
			}
			/*
			if (!ParseState->symbols->add_type(Type->name, Type))
			{
			}
			else
			{
			const glsl_type **NewList = reralloc(ParseState, ParseState->user_structures,
			const glsl_type *,
			ParseState->num_user_structures + 1);
			check(NewList);
			NewList[ParseState->num_user_structures] = Type;
			ParseState->user_structures = NewList;
			ParseState->num_user_structures++;
			}
			*/
		}

		if (PSOut)
		{
			PSOut->remove();
			PreCallInstructions.push_tail(PSOut);
			ParseState->symbols->add_variable(PSOut);
		}
	}

	TIRVarList VarsToMoveToBody;
	foreach_iter(exec_list_iterator, Iter, *Instructions)
	{
		ir_instruction* IR = (ir_instruction*)Iter.get();
		auto* Variable = IR->as_variable();
		if (Variable)
		{
			ir_dereference_variable* ArgVarDeref = NULL;
			switch (Variable->mode)
			{
			case ir_var_in:
				if (PSStageInVariables.find(Variable) != PSStageInVariables.end())
				{
					ir_dereference* DeRefMember = new(ParseState)ir_dereference_record(PSStageIn, Variable->name);
					auto* Assign = new(ParseState)ir_assignment(new(ParseState)ir_dereference_variable(Variable), DeRefMember);
					PreCallInstructions.push_tail(Assign);
					VarsToMoveToBody.push_back(Variable);
				}
				else if (VSStageInVariables.find(Variable) != VSStageInVariables.end())
				{
					ir_rvalue* DeRefMember = new(ParseState)ir_dereference_record(VSStageIn, Variable->name);
					unsigned int Mask = 0;
					if (bExpandVSInputsToFloat4)
					{
						Mask = (1 << 4) - 1;
						auto FoundMember = OriginalVSStageInMembers.find(Variable->name);
						check(FoundMember != OriginalVSStageInMembers.end());
						if (FoundMember->second.type)
						{
							check(FoundMember->second.type->vector_elements != 0);
							Mask = (1 << FoundMember->second.type->vector_elements) - 1;
							if (Mask != 15)
							{
								DeRefMember = new(ParseState)ir_swizzle(DeRefMember, 0, 1, 2, 3, FoundMember->second.type->vector_elements);
							}
						}
					}
					else
					{
						Mask = (1 << Variable->type->vector_elements) - 1;
					}
					auto* Assign = new(ParseState)ir_assignment(new(ParseState)ir_dereference_variable(Variable), DeRefMember, nullptr, Mask);
					PreCallInstructions.push_tail(Assign);
					VarsToMoveToBody.push_back(Variable);
				}
				else
				{
					ArgVarDeref = GenerateShaderInput(
						Frequency,
						ParseState,
						Variable->semantic,
						Variable->type,
						&DeclInstructions,
						&PreCallInstructions
						);
				}
				break;

			case ir_var_out:
				if (VSOutVariables.find(Variable) != VSOutVariables.end())
				{
					VarsToMoveToBody.push_back(Variable);
					ir_dereference* DeRefMember = new(ParseState)ir_dereference_record(VSOut, Variable->name);
					auto* Assign = new(ParseState)ir_assignment(DeRefMember, new(ParseState)ir_dereference_variable(Variable));
					PostCallInstructions.push_tail(Assign);
					/*
					ir_variable* TempVariable = new(ParseState)ir_variable(Variable->type, nullptr, ir_var_temporary);
					PreCallInstructions.push_tail(TempVariable);
					ArgVarDeref = new(ParseState)ir_dereference_variable(TempVariable);*/
				}
				else if (PSOut)
				{
					ArgVarDeref = new(ParseState)ir_dereference_variable(PSOut);
				}
				else
				{
					ArgVarDeref = GenerateShaderOutput(
						Frequency,
						ParseState,
						Variable->semantic,
						Variable->type,
						&DeclInstructions,
						&PreCallInstructions,
						&PostCallInstructions
						);
				}
				break;

			default:
				break;
				/*
				_mesa_glsl_error(
				ParseState,
				"entry point parameter '%s' must be an input or output",
				Variable->name
				);
				*/
			}
			if (ArgVarDeref)
			{
				ArgInstructions.push_tail(ArgVarDeref);
			}
		}
		/*
		else
		{
		_mesa_glsl_error(ParseState, "entry point parameter "
		"'%s' does not specify a semantic", Variable->name);
		}
		*/
	}

	// The function's return value should have an output semantic if it's not void.
	ir_dereference_variable* EntryPointReturn = NULL;
	if (EntryPointSig->return_type->is_void() == false)
	{
		if (Frequency == HSF_PixelShader)
		{
			check(!PSOut);
			PSOut = new(ParseState)ir_variable(EntryPointSig->return_type, "gl_FragColor", ir_var_temporary);
			PreCallInstructions.push_tail(PSOut);
		}
		else if (Frequency == HSF_VertexShader)
		{
			check(!VSOut);
			VSOut = new(ParseState)ir_variable(EntryPointSig->return_type, "__VSOut", ir_var_temporary);
			PreCallInstructions.push_tail(VSOut);
		}
		else
		{
			check(0);
		}
	}

	ParseState->symbols->pop_scope();

	// Build the void main() function for GLSL.
	auto* ReturnType = glsl_type::void_type;
	if (VSOut)
	{
		ReturnType = VSOut->type;
		check(!EntryPointReturn);
		EntryPointReturn = new(ParseState)ir_dereference_variable(VSOut);
		PostCallInstructions.push_tail(new(ParseState)ir_return(new(ParseState)ir_dereference_variable(VSOut)));
		EntryPointSig->return_type = ReturnType;
	}
	else if (PSOut)
	{
		ReturnType = PSOut->type;
		check(!EntryPointReturn);
		EntryPointReturn = new(ParseState)ir_dereference_variable(PSOut);
		PostCallInstructions.push_tail(new(ParseState)ir_return(new(ParseState)ir_dereference_variable(PSOut)));
		EntryPointSig->return_type = ReturnType;
	}

	// main is a reserved keyword...
	RenameMain(Instructions);

	for (auto* Var : VarsToMoveToBody)
	{
		Var->remove();
		if (Var->mode == ir_var_in || Var->mode == ir_var_out)
		{
			Var->mode = ir_var_temporary;
		}
		DeclInstructions.push_head(Var);
	}

	DeclInstructions.append_list(&PreCallInstructions);
	DeclInstructions.append_list(&EntryPointSig->body);
	DeclInstructions.append_list(&PostCallInstructions);

	EntryPointSig->body.append_list(&DeclInstructions);
#if 0
	Instructions->push_tail(MainFunction);
#endif // 0

	// Now that we have a proper main(), move global setup to main().
	if (VSStageIn)
	{
		EntryPointSig->parameters.push_tail(VSStageIn);
	}
	else if (PSStageIn)
	{
		EntryPointSig->parameters.push_tail(PSStageIn);
	}
	/*
	MoveGlobalInstructionsToMain(Instructions);
	*/
}

struct FConvertHalfToFloatUniformAndSamples : public ir_rvalue_visitor
{
	struct FPair
	{
		ir_rvalue** RValuePtr;
		ir_instruction* InsertPoint;
	};
	typedef std::map<ir_rvalue*, std::list<FPair>> TReplacedVarMap;
	TReplacedVarMap ReplacedVars;

	std::list<TReplacedVarMap> PendingReplacements;
	TIRVarSet ReferencedUniforms;

	_mesa_glsl_parse_state* State;

	bool bIsMaster;
	bool bConvertUniforms;
	bool bConvertSamples;

	FConvertHalfToFloatUniformAndSamples(_mesa_glsl_parse_state* InState, bool bInConvertUniforms, bool bInConvertSamples) :
		State(InState),
		bIsMaster(true),
		bConvertUniforms(bInConvertUniforms),
		bConvertSamples(bInConvertSamples)
	{
	}

	void DoConvertOneMap(TReplacedVarMap& Map)
	{
		for (auto& TopIter : Map)
		{
			auto* RValue = TopIter.first;
			auto& List = TopIter.second;

			// Coerce this var into float
			auto* OriginalVar = RValue->variable_referenced();
			const auto* OriginalVarType = OriginalVar->type;
			const auto* PromotedVarType = PromoteHalfToFloatType(State, OriginalVarType);
			OriginalVar->type = PromotedVarType;

			// Temp var and assignment
			auto* NewVar = new(State)ir_variable(RValue->type, nullptr, ir_var_temporary);
			RValue->type = PromoteHalfToFloatType(State, RValue->type);
			exec_list NewAssignments;
			CreateNewAssignmentsFloat2Half(State, NewAssignments, NewVar, RValue);
			auto* BaseIR = List.front().InsertPoint;

			// Store new instructions so we add a nice block in the asm
			BaseIR->insert_before(NewVar);
			BaseIR->insert_before(&NewAssignments);

			for (auto& Iter : List)
			{
				*(Iter.RValuePtr) = new(State)ir_dereference_variable(NewVar);
			}
		}

		// Go through all remaining parameters
		for (auto& Var : ReferencedUniforms)
		{
			Var->type = PromoteHalfToFloatType(State, Var->type);
		}
	}

	void DoConvert(exec_list* ir)
	{
		run(ir);
		DoConvertOneMap(ReplacedVars);

		if (bIsMaster)
		{
			for (auto& Map : PendingReplacements)
			{
				DoConvertOneMap(Map);
			}
		}
	}

	void ConvertBlock(exec_list* instructions)
	{
		FConvertHalfToFloatUniformAndSamples Visitor(State, bConvertUniforms, bConvertSamples);
		Visitor.bIsMaster = false;
		Visitor.run(instructions);
		PendingReplacements.push_back(TReplacedVarMap());
		PendingReplacements.back().swap(Visitor.ReplacedVars);
		for (auto& Var : Visitor.ReferencedUniforms)
		{
			ReferencedUniforms.insert(Var);
		}
	}

	virtual ir_visitor_status visit_enter(ir_if* ir) override
	{
		ir->condition->accept(this);
		handle_rvalue(&ir->condition);

		ConvertBlock(&ir->then_instructions);
		ConvertBlock(&ir->else_instructions);

		/* already descended into the children. */
		return visit_continue_with_parent;
	}

	ir_visitor_status visit_enter(ir_loop* ir)
	{
		ConvertBlock(&ir->body_instructions);

		/* already descended into the children. */
		return visit_continue_with_parent;
	}

	virtual void handle_rvalue(ir_rvalue** RValuePtr) override
	{
		if (!RValuePtr || !*RValuePtr)
		{
			return;
		}

		auto* RValue = *RValuePtr;
		if (bConvertSamples && RValue->as_texture())
		{
			auto* Texture = RValue->as_texture();
			if (Texture->coordinate && Texture->coordinate->type->base_type == GLSL_TYPE_HALF)
			{
				// Promote to float
				Texture->coordinate = new(State)ir_expression(ir_unop_h2f, Texture->coordinate);
			}
		}
		// Skip swizzles, textures, etc
		else if (bConvertUniforms && RValue->as_dereference())
		{
			auto* Var = RValue->variable_referenced();
			if (Var && Var->mode == ir_var_uniform)
			{
				ReferencedUniforms.insert(Var);
				if (RValue->type->base_type == GLSL_TYPE_HALF)
				{
					// Save this RValue and prep for later change
					FPair Pair = {RValuePtr, base_ir};
					for (auto& Iter : ReplacedVars)
					{
						auto* TestRValue = Iter.first;
						if (RValue->ir_type == TestRValue->ir_type && AreEquivalent(RValue, TestRValue))
						{
							Iter.second.push_back(Pair);
							return;
						}
					}

					ReplacedVars[RValue].push_back(Pair);
				}
			}
		}
	}
};


void FMetalCodeBackend::ConvertHalfToFloatUniformsAndSamples(exec_list* ir, _mesa_glsl_parse_state* State, bool bConvertUniforms, bool bConvertSamples)
{
	if (bConvertUniforms || bConvertSamples)
	{
		FConvertHalfToFloatUniformAndSamples ConvertHalfToFloatUniforms(State, bConvertUniforms, bConvertSamples);
		ConvertHalfToFloatUniforms.DoConvert(ir);
	}
}

struct FMetalBreakPrecisionChangesVisitor : public ir_rvalue_visitor
{
	_mesa_glsl_parse_state* State;

	std::map<ir_rvalue*, ir_variable*> ReplacedVars;

	FMetalBreakPrecisionChangesVisitor(_mesa_glsl_parse_state* InState) : State(InState) {}

	void ConvertBlock(exec_list* instructions)
	{
		FMetalBreakPrecisionChangesVisitor Visitor(State);
		Visitor.run(instructions);
	}

	virtual ir_visitor_status visit_enter(ir_if* ir) override
	{
		ir->condition->accept(this);
		handle_rvalue(&ir->condition);

		ConvertBlock(&ir->then_instructions);
		ConvertBlock(&ir->else_instructions);

		/* already descended into the children. */
		return visit_continue_with_parent;
	}

	ir_visitor_status visit_enter(ir_loop* ir)
	{
		ConvertBlock(&ir->body_instructions);

		/* already descended into the children. */
		return visit_continue_with_parent;
	}

	virtual void handle_rvalue(ir_rvalue** RValuePtr) override
	{
		if (!RValuePtr || !*RValuePtr)
		{
			return;
		}
		bool bGenerateNewVar = false;
		auto* RValue = *RValuePtr;
		auto* Expression = RValue->as_expression();
		auto* Constant = RValue->as_constant();
		if (Expression)
		{
			switch (Expression->operation)
			{
			case ir_unop_h2f:
			case ir_unop_f2h:
				if (!Expression->operands[0]->as_texture())
				{
					bGenerateNewVar = true;
				}
				break;
			}
		}
		else if (Constant)
		{
			if (Constant->type->base_type == GLSL_TYPE_HALF)
			{
				bGenerateNewVar = true;
			}
		}

		if (bGenerateNewVar)
		{
			for (auto& Iter : ReplacedVars)
			{
				auto* TestRValue = Iter.first;
				if (AreEquivalent(TestRValue, RValue))
				{
					*RValuePtr = new(State)ir_dereference_variable(Iter.second);
					return;
				}
			}

			auto* NewVar = new(State)ir_variable(RValue->type, nullptr, ir_var_temporary);
			auto* NewAssignment = new(State)ir_assignment(new(State)ir_dereference_variable(NewVar), RValue);
			*RValuePtr = new(State)ir_dereference_variable(NewVar);
			ReplacedVars[RValue] = NewVar;
			base_ir->insert_before(NewVar);
			base_ir->insert_before(NewAssignment);
		}
	}
};


void FMetalCodeBackend::BreakPrecisionChangesVisitor(exec_list* ir, _mesa_glsl_parse_state* State)
{
	FMetalBreakPrecisionChangesVisitor Visitor(State);
	Visitor.run(ir);
}

struct FDeReferencePackedVarsVisitor : public ir_rvalue_visitor
{
	_mesa_glsl_parse_state* State;
	int ExpressionDepth;

	FDeReferencePackedVarsVisitor(_mesa_glsl_parse_state* InState)
		: State(InState)
		, ExpressionDepth(0)
	{
	}

	virtual ir_visitor_status visit_enter(ir_expression* ir) override
	{
		++ExpressionDepth;
		return ir_rvalue_visitor::visit_enter(ir);
	}

	virtual ir_visitor_status visit_leave(ir_expression* ir) override
	{
		for (int i = 0; i < ir->get_num_operands(); ++i)
		{
			auto* Operand = ir->operands[i];
			auto* DerefRecord = Operand->as_dereference_record();
			if (DerefRecord)
			{
				handle_rvalue(&ir->operands[i]);
			}
		}

		--ExpressionDepth;
		return ir_rvalue_visitor::visit_leave(ir);
	}

	virtual void handle_rvalue(ir_rvalue** RValuePtr) override
	{
		if (!RValuePtr || !*RValuePtr)
		{
			return;
		}

		auto* DeRefRecord = (*RValuePtr)->as_dereference_record();
		auto* Swizzle = (*RValuePtr)->as_swizzle();
		auto* SwizzleValDeRefRecord = Swizzle ? Swizzle->val->as_dereference_record() : nullptr;
		if (SwizzleValDeRefRecord)
		{
			auto* StructVar = Swizzle->variable_referenced();
			if (StructVar->type->HlslName && !strcmp(StructVar->type->HlslName, "__PACKED__"))
			{
				if (SwizzleValDeRefRecord->type->vector_elements > 1 && SwizzleValDeRefRecord->type->vector_elements < 4)
				{
					auto* Var = GetVar(SwizzleValDeRefRecord);
					Swizzle->val = new(State)ir_dereference_variable(Var);
				}
			}
		}
		else if (DeRefRecord && ExpressionDepth > 0)
		{
			auto* StructVar = DeRefRecord->variable_referenced();
			if (StructVar->type->HlslName && !strcmp(StructVar->type->HlslName, "__PACKED__"))
			{
				if (DeRefRecord->type->vector_elements > 1 && DeRefRecord->type->vector_elements < 4)
				{
					auto* Var = GetVar(DeRefRecord);
					*RValuePtr = new(State)ir_dereference_variable(Var);
				}
			}
		}
	}

	std::map<ir_dereference_record*, ir_variable*> Replaced;

	ir_variable* GetVar(ir_dereference_record* ir)
	{
		ir_variable* Var = nullptr;
		for (auto& Pair : Replaced)
		{
			if (Pair.first->IsEquivalent(ir))
			{
				Var = Pair.second;
				break;
			}
		}

		if (!Var)
		{
			Var = new(State)ir_variable(ir->type, nullptr, ir_var_temporary);
			Replaced[ir] = Var;
		}
		return Var;
	}
};

void FMetalCodeBackend::RemovePackedVarReferences(exec_list* ir, _mesa_glsl_parse_state* State)
{
	FDeReferencePackedVarsVisitor Visitor(State);
	Visitor.run(ir);

	auto* Main = Visitor.Replaced.empty() ? nullptr : GetMainFunction(ir);
	for (auto& OuterPair : Visitor.Replaced)
	{
		auto* NewVar = OuterPair.second;
		auto* DeRefRecord = OuterPair.first;
		auto* NewAssignment = new(State)ir_assignment(new(State)ir_dereference_variable(NewVar), DeRefRecord);
		Main->body.push_head(NewAssignment);
		Main->body.push_head(NewVar);
	}
}
